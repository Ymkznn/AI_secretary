using AiSecretary.Desktop.Services;
using Xunit;

namespace AiSecretary.Desktop.Tests;

public sealed class StandingWatcherTests
{
    [Fact]
    public void WildcardSnapshot_DetectsChangesDeepInPagePast300Chars_AndProducesFocusedDiff()
    {
        var navPadding = new string('導', 400);
        var htmlV1 = $"""
            <html>
            <head><title>Japan Post Tracking</title><script>var t = 123;</script></head>
            <body>
              <header><nav>郵便番号検索 料金の計算 お届け日数を調べる {navPadding}</nav></header>
              <main>
                <table>
                  <tr><td>11/20/2026 16:42</td><td>Posting/Collection</td><td>TOKYO INT</td></tr>
                  <tr><td>11/21/2026 19:00</td><td>Dispatch from outward office of exchange</td><td>TOKYO INT</td></tr>
                </table>
              </main>
              <footer>Copyright Japan Post</footer>
            </body>
            </html>
            """;

        var htmlV2 = $"""
            <html>
            <head><title>Japan Post Tracking</title><script>var t = 456;</script></head>
            <body>
              <header><nav>郵便番号検索 料金の計算 お届け日数を調べる {navPadding}</nav></header>
              <main>
                <table>
                  <tr><td>11/20/2026 16:42</td><td>Posting/Collection</td><td>TOKYO INT</td></tr>
                  <tr><td>11/21/2026 19:00</td><td>Dispatch from outward office of exchange</td><td>TOKYO INT</td></tr>
                  <tr><td>11/22/2026 09:15</td><td>Arrival at inward office of exchange</td><td>TAIPEI</td></tr>
                </table>
              </main>
              <footer>Copyright Japan Post</footer>
            </body>
            </html>
            """;

        var snap1 = StandingWatcherService.ExtractWildcardPageSnapshot(htmlV1);
        var snap1Again = StandingWatcherService.ExtractWildcardPageSnapshot(htmlV1);
        var snap2 = StandingWatcherService.ExtractWildcardPageSnapshot(htmlV2);

        Assert.StartsWith("sha256:", snap1);
        Assert.False(StandingWatcherService.ShouldTrigger("changed", null, snap1, snap1Again));
        Assert.True(StandingWatcherService.ShouldTrigger("changed", null, snap1, snap2));

        // Diff summary should contain ONLY the newly added EMS row, NOT the nav bar!
        var diff = StandingWatcherService.BuildReadableDiffSummary(snap1, snap2);
        Assert.Contains("Arrival at inward office of exchange", diff);
        Assert.Contains("TAIPEI", diff);
        Assert.DoesNotContain("郵便番号検索", diff);
    }

    [Fact]
    public void WildcardSnapshot_UpgradingFromLegacy300CharSnapshot_DoesNotFalseTrigger()
    {
        var legacySnapshot = "Detailed Search Results - Japan Post 郵便番号検索 料金の計算";
        var newSnap = StandingWatcherService.ExtractWildcardPageSnapshot("<html><body><p>11/21/2026 19:00 Dispatch</p></body></html>");

        // First poll after upgrade baselines cleanly without a false alert
        Assert.False(StandingWatcherService.ShouldTrigger("changed", null, legacySnapshot, newSnap));

        // Subsequent real change triggers normally
        var changedSnap = StandingWatcherService.ExtractWildcardPageSnapshot("<html><body><p>11/21/2026 19:00 Dispatch</p><p>11/22/2026 10:00 Delivered</p></body></html>");
        Assert.True(StandingWatcherService.ShouldTrigger("changed", null, newSnap, changedSnap));
    }
}
