namespace LazyForza.App;

internal static class AssociatedFilePageNavigator
{
    public static void Show(
        int currentPageIndex,
        int targetPageIndex,
        Action<int> selectPage,
        Action renderCurrentPage)
    {
        ArgumentNullException.ThrowIfNull(selectPage);
        ArgumentNullException.ThrowIfNull(renderCurrentPage);

        if (currentPageIndex == targetPageIndex)
        {
            renderCurrentPage();
            return;
        }

        selectPage(targetPageIndex);
    }
}
