using LazyForza.App;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class AssociatedFilePageNavigatorTests
{
    [TestMethod]
    public void SwitchingPageReliesOnSelectionChangedForSingleRender()
    {
        var selectedPage = -1;
        var explicitRenderCount = 0;

        AssociatedFilePageNavigator.Show(
            currentPageIndex: 0,
            targetPageIndex: 5,
            index => selectedPage = index,
            () => explicitRenderCount++);

        Assert.AreEqual(5, selectedPage);
        Assert.AreEqual(0, explicitRenderCount);
    }

    [TestMethod]
    public void CurrentPageIsRenderedToConsumeNewFileRequest()
    {
        var selectionCount = 0;
        var explicitRenderCount = 0;

        AssociatedFilePageNavigator.Show(
            currentPageIndex: 5,
            targetPageIndex: 5,
            _ => selectionCount++,
            () => explicitRenderCount++);

        Assert.AreEqual(0, selectionCount);
        Assert.AreEqual(1, explicitRenderCount);
    }
}
