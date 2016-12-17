using Microsoft.AspNetCore.Mvc;
using NetCoreStack.Test.Common;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebClient.Controllers
{
    [Route("api/[controller]")]
    public class TestController : Controller
    {
        private readonly IGuidelineApi _api;

        public TestController(IGuidelineApi api)
        {
            _api = api;
        }

        [HttpGet]
        public async Task<IEnumerable<Post>> Get()
        {
            var items = await _api.GetPostsAsync();
            return items;
        }
    }
}
