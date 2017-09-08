using Microsoft.AspNetCore.Mvc;
using NetCoreStack.Contracts;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NetCoreStack.Test.Common
{
    public interface IGuidelineApi : IApiContract
    {
        void VoidOperation();

        ApiResult Operation(int i, string s, long l, DateTime dt);

        int PrimitiveReturn(int i, string s, long l, DateTime dt);

        Task TaskOperation();

        Task<ApiResult> TaskApiResult(int i, string s, long l, DateTime dt);

        Task<IEnumerable<Post>> GetPostsAsync();

        [HttpPost]
        Task<ApiResult> TaskActionPost(SimpleModel model);

        ApiResult<bool> ReturnBoolean();

        ApiResult ThrowException();
    }
}
