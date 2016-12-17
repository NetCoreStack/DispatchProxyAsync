using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;

namespace NetCoreStack.Test.Common
{
    public class HttpDispatchProxyAsync : DispatchProxyAsync
    {
        public HttpDispatchProxyAsync()
        {

        }

        private async Task<HttpResponseMessage> InvokeInternal()
        {
            var httpClient = new HttpClient();
            httpClient.BaseAddress = new Uri("https://jsonplaceholder.typicode.com/");
            var httpRequest = new HttpRequestMessage(HttpMethod.Get, "posts");

            await Task.Delay(1000);

            var response = await httpClient.SendAsync(httpRequest);
            return response;
        }

        public override async Task InvokeAsync(MethodInfo method, object[] args)
        {
            await InvokeInternal();
        }

        public override async Task<T> InvokeAsyncT<T>(MethodInfo method, object[] args)
        {
            var response = await InvokeInternal();
            var content = await response.Content.ReadAsStringAsync();
            var instance = JsonConvert.DeserializeObject<T>(content);
            return instance;
        }

        public override object Invoke(MethodInfo method, object[] args)
        {
            return new object();
        }
    }
}
