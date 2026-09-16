using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixiEditor.ChangeableDocument.Changeables;
using PixiEditor.ChangeableDocument.Changeables.Graph.Interfaces;
using PixiDocks.Core.Docking;
using PixiEditor.ViewModels.Document;

namespace PixiEditor.ViewModels.Dock;

internal class OcclusionGraphDockViewModel : DockableViewModel
{
    public const string TabId = "OcclusionGraph";

    public override string Id => TabId;
    public override string Title => "Occlusion Graph (experimental)";
    public override bool CanFloat => true;
    public override bool CanClose => true;

    public DocumentManagerViewModel DocumentManagerSubViewModel { get; }

    private DocumentViewModel? activeDocument;
    private OcclusionGraph? observedGraph;
    private OcclusionLayerOptionViewModel? frontLayer;
    private OcclusionLayerOptionViewModel? backLayer;
    private OcclusionRelationViewModel? selectedRelation;
    private string statusText = "ドキュメントを開いてください。";
    private string conflictText = string.Empty;
    private bool isRefreshing;
    private double graphZoom = 1;

    public DocumentViewModel? ActiveDocument
    {
        get => activeDocument;
        private set => SetProperty(ref activeDocument, value);
    }

    public ObservableCollection<OcclusionLayerOptionViewModel> Layers { get; } = new();
    public ObservableCollection<OcclusionGraphNodeViewModel> GraphNodes { get; } = new();
    public ObservableCollection<OcclusionRelationViewModel> Relations { get; } = new();

    public double GraphZoom
    {
        get => graphZoom;
        private set
        {
            if (SetProperty(ref graphZoom, value))
                OnPropertyChanged(nameof(GraphZoomText));
        }
    }

    public string GraphZoomText => $"{GraphZoom:P0}";

    public OcclusionLayerOptionViewModel? FrontLayer
    {
        get => frontLayer;
        set
        {
            if (SetProperty(ref frontLayer, value))
                AddRelationCommand.NotifyCanExecuteChanged();
        }
    }

    public OcclusionLayerOptionViewModel? BackLayer
    {
        get => backLayer;
        set
        {
            if (SetProperty(ref backLayer, value))
                AddRelationCommand.NotifyCanExecuteChanged();
        }
    }

    public OcclusionRelationViewModel? SelectedRelation
    {
        get => selectedRelation;
        set => SetProperty(ref selectedRelation, value);
    }

    public bool IsEnabled
    {
        get => ActiveDocument?.OcclusionGraph.IsEnabled ?? false;
        set
        {
            if (isRefreshing || ActiveDocument is null || value == IsEnabled)
                return;

            ActiveDocument.Operations.SetOcclusionGraphEnabled(value);
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public string ConflictText
    {
        get => conflictText;
        private set => SetProperty(ref conflictText, value);
    }

    public RelayCommand AddRelationCommand { get; }
    public RelayCommand RemoveRelationCommand { get; }
    public RelayCommand ReverseRelationCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand ResetGraphViewCommand { get; }

    public OcclusionGraphDockViewModel(DocumentManagerViewModel documentManager)
    {
        DocumentManagerSubViewModel = documentManager;
        AddRelationCommand = new RelayCommand(AddRelation, CanAddRelation);
        RemoveRelationCommand = new RelayCommand(RemoveRelation);
        ReverseRelationCommand = new RelayCommand(ReverseRelation);
        RefreshCommand = new RelayCommand(Refresh);
        ZoomInCommand = new RelayCommand(() => GraphZoom = Math.Min(2.0, GraphZoom + 0.1));
        ZoomOutCommand = new RelayCommand(() => GraphZoom = Math.Max(0.5, GraphZoom - 0.1));
        ResetGraphViewCommand = new RelayCommand(() =>
        {
            GraphZoom = 1;
            ResetGraphNodePositions();
        });

        documentManager.ActiveDocumentChanged += DocumentManager_ActiveDocumentChanged;
        AttachDocument(documentManager.ActiveDocument);
    }

    private void DocumentManager_ActiveDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        AttachDocument(e.NewDocument);
    }

    private void AttachDocument(DocumentViewModel? document)
    {
        if (observedGraph is not null)
            observedGraph.Changed -= ObservedGraph_Changed;

        ActiveDocument = document;
        observedGraph = document?.OcclusionGraph;
        graphNodePositions.Clear();
        GraphZoom = 1;
        if (observedGraph is not null)
            observedGraph.Changed += ObservedGraph_Changed;

        Refresh();
    }

    private void ObservedGraph_Changed(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh()
    {
        isRefreshing = true;
        try
        {
            Layers.Clear();
            GraphNodes.Clear();
            Relations.Clear();
            FrontLayer = null;
            BackLayer = null;
            SelectedRelation = null;

            DocumentViewModel? document = ActiveDocument;
            OcclusionGraph? graph = document?.OcclusionGraph;
            if (document is null || graph is null)
            {
                StatusText = "ドキュメントを開いてください。";
                ConflictText = string.Empty;
                OnPropertyChanged(nameof(IsEnabled));
                return;
            }

            Dictionary<Guid, OcclusionLayerOptionViewModel> layerMap = new();
            List<IReadOnlyLayerNode> layerNodes = document.AccessInternalReadOnlyDocument()
                .GetStructureTreeInOrder()
                .OfType<IReadOnlyLayerNode>()
                .ToList();

            HashSet<OcclusionRelation> ignoredRelations = FindIgnoredRelations(graph.Relations);
            HashSet<Guid> conflictLayerIds = ignoredRelations
                .SelectMany(x => new[] { x.FrontLayerId, x.BackLayerId })
                .ToHashSet();
            HashSet<Guid> cycleLayerIds = FindCycleLayerIds(graph.Relations);
            conflictLayerIds.UnionWith(cycleLayerIds);

            for (int index = 0; index < layerNodes.Count; index++)
            {
                IReadOnlyLayerNode member = layerNodes[index];

                string name = string.IsNullOrWhiteSpace(member.MemberName)
                    ? $"Layer {member.Id.ToString()[..8]}"
                    : member.MemberName;
                OcclusionLayerOptionViewModel option = new(member.Id, name);
                layerMap[member.Id] = option;
                Layers.Add(option);

                (double x, double y) = GetGraphNodePosition(member.Id, index);
                GraphNodes.Add(new OcclusionGraphNodeViewModel(
                    member.Id, name, x, y, conflictLayerIds.Contains(member.Id)));
            }

            foreach (OcclusionRelation relation in graph.Relations)
            {
                if (!layerMap.TryGetValue(relation.FrontLayerId, out OcclusionLayerOptionViewModel? front) ||
                    !layerMap.TryGetValue(relation.BackLayerId, out OcclusionLayerOptionViewModel? back))
                {
                    continue;
                }

                Relations.Add(new OcclusionRelationViewModel(relation, front.DisplayName, back.DisplayName,
                    ignoredRelations.Contains(relation)));
            }

            StatusText = Relations.Count == 0
                ? "関係はまだありません。"
                : $"{Relations.Count} 件の関係を編集中。";
            int cycleEdgeCount = CountCycleEdges(graph.Relations);
            ConflictText = ignoredRelations.Count > 0
                ? $"コンフリクトで無視: {ignoredRelations.Count} 件（関係一覧を上から処理）"
                : cycleEdgeCount > 0
                    ? $"循環候補: {cycleEdgeCount} 辺（同一点で矛盾する関係を無視します）"
                    : "同一点で矛盾が生じた場合は、関係一覧を上から処理し、原因になった関係を無視します。未対応の効果は通常順へフォールバックします。";
            OnPropertyChanged(nameof(IsEnabled));
        }
        finally
        {
            isRefreshing = false;
            AddRelationCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanAddRelation()
    {
        return ActiveDocument is not null && FrontLayer is not null && BackLayer is not null &&
               FrontLayer.Id != BackLayer.Id;
    }

    private void AddRelation()
    {
        if (!CanAddRelation())
            return;

        ActiveDocument!.Operations.SetOcclusionRelation(FrontLayer!.Id, BackLayer!.Id, true);
    }

    private void RemoveRelation()
    {
        if (ActiveDocument is null || SelectedRelation is null)
            return;

        ActiveDocument.Operations.SetOcclusionRelation(
            SelectedRelation.FrontLayerId, SelectedRelation.BackLayerId, false);
    }

    private void ReverseRelation()
    {
        if (ActiveDocument is null || SelectedRelation is null)
            return;

        ActiveDocument.Operations.ReverseOcclusionRelation(
            SelectedRelation.FrontLayerId, SelectedRelation.BackLayerId);
    }

    internal void UpdateGraphNodePosition(Guid id, double x, double y)
    {
        x = Math.Clamp(x, 0, 560);
        y = Math.Clamp(y, 0, 170);
        graphNodePositions[id] = (x, y);

        OcclusionGraphNodeViewModel? node = GraphNodes.FirstOrDefault(x => x.Id == id);
        if (node is null)
            return;

        node.X = x;
        node.Y = y;
    }

    private (double x, double y) GetGraphNodePosition(Guid id, int index)
    {
        if (graphNodePositions.TryGetValue(id, out (double x, double y) position))
            return position;

        position = (16 + index % 3 * 190, 16 + index / 3 * 64);
        graphNodePositions[id] = position;
        return position;
    }

    private void ResetGraphNodePositions()
    {
        graphNodePositions.Clear();
        Refresh();
    }

    private static HashSet<OcclusionRelation> FindIgnoredRelations(
        IReadOnlyList<OcclusionRelation> relations)
    {
        HashSet<OcclusionRelation> ignoredRelations = new();
        List<OcclusionRelation> acceptedRelations = new();

        foreach (OcclusionRelation relation in relations)
        {
            if (HasRelationPath(relation.BackLayerId, relation.FrontLayerId, acceptedRelations, relation,
                    new HashSet<Guid>()))
            {
                ignoredRelations.Add(relation);
                continue;
            }

            acceptedRelations.Add(relation);
        }

        return ignoredRelations;
    }

    private static int CountCycleEdges(IReadOnlyList<OcclusionRelation> relations)
    {
        return relations.Count(relation => HasRelationPath(
            relation.BackLayerId, relation.FrontLayerId, relations, relation,
            new HashSet<Guid>()));
    }

    private static HashSet<Guid> FindCycleLayerIds(IReadOnlyList<OcclusionRelation> relations)
    {
        HashSet<Guid> cycleLayerIds = new();
        foreach (OcclusionRelation relation in relations)
        {
            if (!HasRelationPath(relation.BackLayerId, relation.FrontLayerId, relations, relation,
                    new HashSet<Guid>()))
                continue;

            cycleLayerIds.Add(relation.FrontLayerId);
            cycleLayerIds.Add(relation.BackLayerId);
        }

        return cycleLayerIds;
    }

    private static bool HasRelationPath(Guid current, Guid target,
        IReadOnlyList<OcclusionRelation> relations, OcclusionRelation ignored, HashSet<Guid> visited)
    {
        if (current == target)
            return true;

        if (!visited.Add(current))
            return false;

        foreach (OcclusionRelation relation in relations)
        {
            if (relation == ignored || relation.FrontLayerId != current)
                continue;

            if (HasRelationPath(relation.BackLayerId, target, relations, ignored, visited))
                return true;
        }

        return false;
    }

    private readonly Dictionary<Guid, (double x, double y)> graphNodePositions = new();
}

internal sealed class OcclusionLayerOptionViewModel
{
    public Guid Id { get; }
    public string DisplayName { get; }

    public OcclusionLayerOptionViewModel(Guid id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }
}

internal sealed class OcclusionGraphNodeViewModel : ObservableObject
{
    private double x;
    private double y;

    public Guid Id { get; }
    public string DisplayName { get; }
    public bool IsConflict { get; }

    public double X
    {
        get => x;
        set => SetProperty(ref x, value);
    }

    public double Y
    {
        get => y;
        set => SetProperty(ref y, value);
    }

    public OcclusionGraphNodeViewModel(Guid id, string displayName, double x, double y, bool isConflict)
    {
        Id = id;
        DisplayName = displayName;
        this.x = x;
        this.y = y;
        IsConflict = isConflict;
    }
}

internal sealed class OcclusionRelationViewModel
{
    public Guid FrontLayerId { get; }
    public Guid BackLayerId { get; }
    public string DisplayText { get; }
    public bool IsIgnored { get; }

    public OcclusionRelationViewModel(OcclusionRelation relation, string frontName, string backName,
        bool isIgnored)
    {
        FrontLayerId = relation.FrontLayerId;
        BackLayerId = relation.BackLayerId;
        DisplayText = $"{frontName}  >  {backName}";
        IsIgnored = isIgnored;
    }
}
