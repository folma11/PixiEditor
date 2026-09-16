using PixiEditor.ChangeableDocument.Changeables;

namespace PixiEditor.ChangeableDocument.Tests;

public class OcclusionGraphTests
{
    [Fact]
    public void RelationsCanBeAddedAndRemoved()
    {
        OcclusionGraph graph = new();
        Guid front = Guid.NewGuid();
        Guid back = Guid.NewGuid();

        Assert.True(graph.SetRelation(front, back, true));
        Assert.True(graph.HasRelation(front, back));
        Assert.False(graph.SetRelation(front, back, true));

        Assert.True(graph.SetRelation(front, back, false));
        Assert.False(graph.HasRelation(front, back));
    }

    [Fact]
    public void ReciprocalRelationsAreReportedAsConflicts()
    {
        OcclusionGraph graph = new();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        graph.SetRelation(first, second, true);
        graph.SetRelation(second, first, true);

        Assert.Equal(1, graph.ReciprocalConflictCount);
        Assert.True(graph.IsReciprocalConflict(new OcclusionRelation(first, second)));
    }

    [Fact]
    public void CyclesAreAllowedAndClonesAreIndependent()
    {
        OcclusionGraph graph = new();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        Guid third = Guid.NewGuid();

        graph.SetRelation(first, second, true);
        graph.SetRelation(second, third, true);
        graph.SetRelation(third, first, true);
        graph.SetEnabled(true);

        OcclusionGraph clone = graph.Clone();

        Assert.Equal(3, clone.Relations.Count);
        Assert.True(clone.IsEnabled);
        clone.SetRelation(first, second, false);
        Assert.True(graph.HasRelation(first, second));
    }

    [Fact]
    public void SelfRelationsAreIgnored()
    {
        OcclusionGraph graph = new();
        Guid layer = Guid.NewGuid();

        Assert.False(graph.SetRelation(layer, layer, true));
        Assert.Empty(graph.Relations);
    }

    [Fact]
    public void RelationsPreserveInsertionPriority()
    {
        OcclusionGraph graph = new();
        OcclusionRelation first = new(Guid.NewGuid(), Guid.NewGuid());
        OcclusionRelation second = new(Guid.NewGuid(), Guid.NewGuid());

        graph.SetRelation(first.FrontLayerId, first.BackLayerId, true);
        graph.SetRelation(second.FrontLayerId, second.BackLayerId, true);

        Assert.Equal(first, graph.Relations[0]);
        Assert.Equal(second, graph.Relations[1]);
    }
}
