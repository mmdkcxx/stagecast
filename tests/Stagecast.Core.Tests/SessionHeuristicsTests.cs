using Stagecast.Core.Sessions;

namespace Stagecast.Core.Tests;

public class SessionHeuristicsTests
{
    [Theory]
    [InlineData("about:blank")]
    [InlineData("file:///C:/slides/index.html")]
    [InlineData("data:text/html,hello")]
    [InlineData("")]
    [InlineData(null)]
    public void NotAWebsite_HasNothingToLogInTo(string? url)
        => Assert.Equal(SessionState.NoLogin, SessionHeuristics.Classify(url, sessionCookies: 5));

    [Theory]
    [InlineData("https://accounts.google.com/v3/signin/identifier")]
    [InlineData("https://www.canva.com/login")]
    [InlineData("https://www.canva.com/login/")]
    [InlineData("https://kahoot.it/login?next=/")]
    [InlineData("https://app.slido.com/sign-in")]
    [InlineData("https://example.com/users/sign_in")]
    [InlineData("https://example.com/auth/callback")]
    [InlineData("https://login.microsoftonline.com/common/oauth2")]
    [InlineData("https://example.com/accounts/login/")]
    [InlineData("https://example.com/masuk")]
    public void LoginPages_NeedLogin_EvenWithCookies(string url)
        => Assert.Equal(SessionState.NeedsLogin, SessionHeuristics.Classify(url, sessionCookies: 3));

    [Theory]
    [InlineData("https://www.canva.com/design/DAF123/edit")]
    [InlineData("https://create.kahoot.it/creator")]
    [InlineData("https://app.slido.com/events/123")]
    [InlineData("https://example.com/author/profile")]   // "auth" must not match "author"
    [InlineData("https://example.com/authentic")]
    public void OrdinaryPages_WithASessionCookie_AreLoggedIn(string url)
        => Assert.Equal(SessionState.LoggedIn, SessionHeuristics.Classify(url, sessionCookies: 1));

    [Fact]
    public void WebsiteWithoutSessionCookie_NeedsLogin()
        => Assert.Equal(SessionState.NeedsLogin, SessionHeuristics.Classify("https://www.canva.com/design/DAF123/edit", sessionCookies: 0));
}
