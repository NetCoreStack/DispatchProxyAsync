// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Reflection;
using System.Threading.Tasks;

namespace NetCoreStack.Test.Common
{
    // Test types used to make proxies.
    public interface TestType_IHelloService
    {
        string Hello(string message);
    }

    public interface TestType_IGoodbyeService
    {
        string Goodbye(string message);
    }

    // Demonstrates interface implementing multiple other interfaces
    public interface TestType_IHelloAndGoodbyeService : TestType_IHelloService, TestType_IGoodbyeService
    {
    }

    // Deliberately contains method with same signature of TestType_IHelloService (see TestType_IHelloService1And2).
    public interface TestType_IHelloService2
    {
        string Hello(string message);
    }

    // Demonstrates 2 interfaces containing same method name dispatches to the right one
    public interface TestType_IHelloService1And2 : TestType_IHelloService, TestType_IHelloService2
    {
    }

    // Demonstrates methods taking multiple parameters as well as a params parameter
    public interface TestType_IMultipleParameterService
    {
        double TestMethod(int i, string s, double d);
        object ParamsMethod(params object[] parameters);
    }

    // Demonstrate a void-returning method and parameterless method
    public interface TestType_IOneWay
    {
        void OneWay();
    }

    // Demonstrates proxies can be made for properties.
    public interface TestType_IPropertyService
    {
        string ReadWrite { get; set; }
    }

    // Demonstrates proxies can be made for events.
    public interface TestType_IEventService
    {
        event EventHandler AddRemove;
    }

    // Demonstrates proxies can be made for indexed properties.
    public interface TestType_IIndexerService
    {
        string this[string key] { get; set; }
    }

    // Negative -- demonstrates trying to use a class for the interface type for the proxy
    public class TestType_ConcreteClass
    {
        public string Echo(string s) { return null; }
    }

    // Negative -- demonstrates base type that is sealed and should generate exception
    public sealed class Sealed_TestDispatchProxyAsync : DispatchProxyAsync
    {
        public override object Invoke(MethodInfo method, object[] args)
        {
            throw new NotImplementedException();
        }

        public override Task InvokeAsync(MethodInfo method, object[] args)
        {
            throw new NotImplementedException();
        }

        public override Task<T> InvokeAsyncT<T>(MethodInfo method, object[] args)
        {
            throw new NotImplementedException();
        }
    }

    // This test double creates a proxy instance for the requested 'ProxyT' type.
    // When methods are invoked on that proxy, it will call a registered callback.
    public class TestDispatchProxyAsync : DispatchProxyAsync
    {
        // Gets or sets the Action to invoke when clients call methods on the proxy.
        public Func<MethodInfo, object[], object> CallOnInvoke { get; set; }

        public Func<MethodInfo, object[], Task> CallOnInvokeAsync { get; set; }

        public Func<MethodInfo, object[], Task<object>> CallOnInvokeAsyncT { get; set; }

        // Gets the proxy itself (which is always 'this')
        public object GetProxy()
        {
            return this;
        }

        public override object Invoke(MethodInfo method, object[] args)
        {
            return CallOnInvoke(method, args);
        }

        public override async Task InvokeAsync(MethodInfo method, object[] args)
        {
            await CallOnInvokeAsync(method, args);
        }

        public override async Task<T> InvokeAsyncT<T>(MethodInfo method, object[] args)
        {
            var result = await CallOnInvokeAsyncT(method, args);
            return (T)result;
        }
    }

    public class TestDispatchProxyAsync2 : TestDispatchProxyAsync
    {
    }

    // Negative test -- demonstrates base type that is abstract
    public abstract class Abstract_TestDispatchProxyAsync : DispatchProxyAsync
    {
    }

    // Negative -- demonstrates base type that has no public default ctor
    public class NoDefaultCtor_TestDispatchProxyAsync : DispatchProxyAsync
    {
        private NoDefaultCtor_TestDispatchProxyAsync()
        {
        }

        public override object Invoke(MethodInfo method, object[] args)
        {
            throw new NotImplementedException();
        }

        public override Task InvokeAsync(MethodInfo method, object[] args)
        {
            throw new NotImplementedException();
        }

        public override Task<T> InvokeAsyncT<T>(MethodInfo method, object[] args)
        {
            throw new NotImplementedException();
        }
    }
}

