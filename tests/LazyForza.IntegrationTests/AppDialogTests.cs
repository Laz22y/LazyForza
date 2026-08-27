using System.Windows;
using LazyForza.App;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class AppDialogTests
{
    [DataTestMethod]
    [DataRow(MessageBoxButton.OK, MessageBoxResult.OK, 1)]
    [DataRow(MessageBoxButton.OKCancel, MessageBoxResult.Cancel, 2)]
    [DataRow(MessageBoxButton.YesNo, MessageBoxResult.No, 2)]
    [DataRow(MessageBoxButton.YesNoCancel, MessageBoxResult.Cancel, 3)]
    public void StandardButtonsPreserveSafeCloseResult(
        MessageBoxButton buttons,
        MessageBoxResult expectedSafeResult,
        int expectedButtonCount)
    {
        var layout = AppDialog.StandardButtons(buttons);

        Assert.AreEqual(expectedSafeResult, layout.SafeResult);
        Assert.AreEqual(expectedButtonCount, layout.Buttons.Count);
        Assert.AreEqual(1, layout.Buttons.Count(button => button.IsPrimary));
        Assert.IsTrue(layout.Buttons.Any(button => button.Result == expectedSafeResult));
    }

    [TestMethod]
    public void YesNoCancelKeepsAllNativeResults()
    {
        var results = AppDialog.StandardButtons(MessageBoxButton.YesNoCancel)
            .Buttons
            .Select(button => button.Result)
            .ToArray();

        CollectionAssert.AreEquivalent(
            new[] { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel },
            results);
    }
}
