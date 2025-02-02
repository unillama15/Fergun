using System.Net.Http;
using System.Threading.Tasks;
using GScraper;
using GScraper.Brave;
using GScraper.DuckDuckGo;
using GScraper.Google;
using Xunit;

namespace Fergun.Tests
{
    public class GScraperTests
    {
        [Theory]
        [InlineData("Hello world", SafeSearchLevel.Off)]
        [InlineData("Discord", SafeSearchLevel.Moderate)]
        [InlineData("Dogs", SafeSearchLevel.Strict)]
        [InlineData("Cats", SafeSearchLevel.Off)]
        public async Task GScraperAvailableTest(string query, SafeSearchLevel safeSearch)
        {
            // Arrange
            var googleScraper = new GoogleScraper();

            var ddgClient = new HttpClient();
            ddgClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:77.0) Gecko/20100101 Firefox/77.0");
            var ddgScraper = new DuckDuckGoScraper(ddgClient);

            var braveScraper = new BraveScraper();

            // Act
            var googleImages = await googleScraper.GetImagesAsync(query, safeSearch);
            var ddgImages = await ddgScraper.GetImagesAsync(query, safeSearch);
            var braveImages = await braveScraper.GetImagesAsync(query, safeSearch);

            // Assert
            Assert.NotEmpty(googleImages);
            Assert.NotEmpty(ddgImages);
            Assert.NotEmpty(braveImages);
        }
    }
}