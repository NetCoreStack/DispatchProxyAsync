using NetCoreStack.Common;
using System;
using System.Reflection;

namespace NetCoreStack.Test.Common
{
    public static class ProxyHelper
    {
        public static TProxy CreateProxy<TProxy>(IServiceProvider container) where TProxy : IDependency
        {
            dynamic proxy = DispatchProxyAsync.Create<TProxy, HttpDispatchProxyAsync>();
            return proxy;
        }
    }
}
