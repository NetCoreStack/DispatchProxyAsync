﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace System.Reflection
{
    public class DispatchProxyHandler
    {
        public DispatchProxyHandler()
        {
        }

        public object InvokeHandle(object[] args)
        {
            return AsyncDispatchProxyGenerator.Invoke(args);
        }

        public Task InvokeAsyncHandle(object[] args)
        {
            return AsyncDispatchProxyGenerator.InvokeAsync(args);
        }

        public Task<T> InvokeAsyncHandleT<T>(object[] args)
        {
            return AsyncDispatchProxyGenerator.InvokeAsync<T>(args);
        }
    }

    // Helper class to handle the IL EMIT for the generation of proxies.
    // Much of this code was taken directly from the Silverlight proxy generation.
    // Differences between this and the Silverlight version are:
    //  1. This version is based on DispatchProxy from NET Native and CoreCLR, not RealProxy in Silverlight ServiceModel.
    //     There are several notable differences between them.
    //  2. Both DispatchProxy and RealProxy permit the caller to ask for a proxy specifying a pair of types:
    //     the interface type to implement, and a base type.  But they behave slightly differently:
    //       - RealProxy generates a proxy type that derives from Object and *implements" all the base type's
    //         interfaces plus all the interface type's interfaces.
    //       - DispatchProxy generates a proxy type that *derives* from the base type and implements all
    //         the interface type's interfaces.  This is true for both the CLR version in NET Native and this
    //         version for CoreCLR.
    //  3. DispatchProxy and RealProxy use different type hierarchies for the generated proxies:
    //       - RealProxy type hierarchy is:
    //             proxyType : proxyBaseType : object
    //         Presumably the 'proxyBaseType' in the middle is to allow it to implement the base type's interfaces
    //         explicitly, preventing collision for same name methods on the base and interface types.
    //       - DispatchProxy hierarchy is:
    //             proxyType : baseType (where baseType : DispatchProxy)
    //         The generated DispatchProxy proxy type does not need to generate implementation methods
    //         for the base type's interfaces, because the base type already must have implemented them.
    //  4. RealProxy required a proxy instance to hold a backpointer to the RealProxy instance to mirror
    //     the .Net Remoting design that required the proxy and RealProxy to be separate instances.
    //     But the DispatchProxy design encourages the proxy type to *be* an DispatchProxy.  Therefore,
    //     the proxy's 'this' becomes the equivalent of RealProxy's backpointer to RealProxy, so we were
    //     able to remove an extraneous field and ctor arg from the DispatchProxy proxies.
    //
    internal static class AsyncDispatchProxyGenerator
    {
        private const int InvokeActionFieldAndCtorParameterIndex = 0;

        // Proxies are requested for a pair of types: base type and interface type.
        // The generated proxy will subclass the given base type and implement the interface type.
        // We maintain a cache keyed by 'base type' containing a dictionary keyed by interface type,
        // containing the generated proxy type for that pair.   There are likely to be few (maybe only 1)
        // base type in use for many interface types.
        // Note: this differs from Silverlight's RealProxy implementation which keys strictly off the
        // interface type.  But this does not allow the same interface type to be used with more than a
        // single base type.  The implementation here permits multiple interface types to be used with
        // multiple base types, and the generated proxy types will be unique.
        // This cache of generated types grows unbounded, one element per unique T/ProxyT pair.
        // This approach is used to prevent regenerating identical proxy types for identical T/Proxy pairs,
        // which would ultimately be a more expensive leak.
        // Proxy instances are not cached.  Their lifetime is entirely owned by the caller of DispatchProxy.Create.
        private static readonly Dictionary<Type, Dictionary<Type, Type>> s_baseTypeAndInterfaceToGeneratedProxyType = 
            new Dictionary<Type, Dictionary<Type, Type>>();

        private static readonly ProxyAssembly s_proxyAssembly = new ProxyAssembly();
        private static readonly MethodInfo s_dispatchProxyInvokeMethod = typeof(DispatchProxyAsync).GetTypeInfo().GetDeclaredMethod("Invoke");
        private static readonly MethodInfo s_dispatchProxyInvokeAsyncMethod = typeof(DispatchProxyAsync).GetTypeInfo().GetDeclaredMethod("InvokeAsync");
        private static readonly MethodInfo s_dispatchProxyInvokeAsyncTMethod = typeof(DispatchProxyAsync).GetTypeInfo().GetDeclaredMethod("InvokeAsyncT");

        // Returns a new instance of a proxy the derives from 'baseType' and implements 'interfaceType'
        internal static object CreateProxyInstance(Type baseType, Type interfaceType)
        {
            Debug.Assert(baseType != null);
            Debug.Assert(interfaceType != null);

            Type proxiedType = GetProxyType(baseType, interfaceType);
            return Activator.CreateInstance(proxiedType, new DispatchProxyHandler());
        }

        private static Type GetProxyType(Type baseType, Type interfaceType)
        {
            lock (s_baseTypeAndInterfaceToGeneratedProxyType)
            {
                Dictionary<Type, Type> interfaceToProxy = null;
                if (!s_baseTypeAndInterfaceToGeneratedProxyType.TryGetValue(baseType, out interfaceToProxy))
                {
                    interfaceToProxy = new Dictionary<Type, Type>();
                    s_baseTypeAndInterfaceToGeneratedProxyType[baseType] = interfaceToProxy;
                }

                Type generatedProxy = null;
                if (!interfaceToProxy.TryGetValue(interfaceType, out generatedProxy))
                {
                    generatedProxy = GenerateProxyType(baseType, interfaceType);
                    interfaceToProxy[interfaceType] = generatedProxy;
                }

                return generatedProxy;
            }
        }

        // Unconditionally generates a new proxy type derived from 'baseType' and implements 'interfaceType'
        private static Type GenerateProxyType(Type baseType, Type interfaceType)
        {
            // Parameter validation is deferred until the point we need to create the proxy.
            // This prevents unnecessary overhead revalidating cached proxy types.
            TypeInfo baseTypeInfo = baseType.GetTypeInfo();

            // The interface type must be an interface, not a class
            if (!interfaceType.GetTypeInfo().IsInterface)
            {
                // "T" is the generic parameter seen via the public contract
                throw new ArgumentException($"InterfaceType_Must_Be_Interface, {interfaceType.FullName}", "T");
            }

            // The base type cannot be sealed because the proxy needs to subclass it.
            if (baseTypeInfo.IsSealed)
            {
                // "TProxy" is the generic parameter seen via the public contract
                throw new ArgumentException($"BaseType_Cannot_Be_Sealed, {baseTypeInfo.FullName}", "TProxy");
            }

            // The base type cannot be abstract
            if (baseTypeInfo.IsAbstract)
            {
                throw new ArgumentException($"BaseType_Cannot_Be_Abstract {baseType.FullName}", "TProxy");
            }

            // The base type must have a public default ctor
            if (!baseTypeInfo.DeclaredConstructors.Any(c => c.IsPublic && c.GetParameters().Length == 0))
            {
                throw new ArgumentException($"BaseType_Must_Have_Default_Ctor {baseType.FullName}", "TProxy");
            }

            // Create a type that derives from 'baseType' provided by caller
            ProxyBuilder pb = s_proxyAssembly.CreateProxy("generatedProxy", baseType);

            foreach (Type t in interfaceType.GetTypeInfo().ImplementedInterfaces)
                pb.AddInterfaceImpl(t);

            pb.AddInterfaceImpl(interfaceType);

            Type generatedProxyType = pb.CreateType();
            return generatedProxyType;
        }

        private class ProxyMethodResolverContext
        {
            public PackedArgs Packed { get; }
            public MethodBase Method { get; }

            public ProxyMethodResolverContext(PackedArgs packed, MethodBase method)
            {
                Packed = packed;
                Method = method;
            }
        }

        private static ProxyMethodResolverContext Resolve(object[] args)
        {
            PackedArgs packed = new PackedArgs(args);
            MethodBase method = s_proxyAssembly.ResolveMethodToken(packed.DeclaringType, packed.MethodToken);
            if (method.IsGenericMethodDefinition)
                method = ((MethodInfo)method).MakeGenericMethod(packed.GenericTypes);

            return new ProxyMethodResolverContext(packed, method);
        }

        public static object Invoke(object[] args)
        {
            var context = Resolve(args);

            // Call (protected method) DispatchProxyAsync.Invoke()
            object returnValue = null;
            try
            {
                Debug.Assert(s_dispatchProxyInvokeMethod != null);
                returnValue = s_dispatchProxyInvokeMethod.Invoke(context.Packed.DispatchProxy,
                                                                       new object[] { context.Method, context.Packed.Args });
                context.Packed.ReturnValue = returnValue;
            }
            catch (TargetInvocationException tie)
            {
                ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            }

            return returnValue;
        }

        public static async Task InvokeAsync(object[] args)
        {
            var context = Resolve(args);

            // Call (protected Task method) NetCoreStackDispatchProxy.InvokeAsync()
            try
            {
                Debug.Assert(s_dispatchProxyInvokeAsyncMethod != null);
                await (Task) s_dispatchProxyInvokeAsyncMethod.Invoke(context.Packed.DispatchProxy,
                                                                       new object[] { context.Method, context.Packed.Args });
            }
            catch (TargetInvocationException tie)
            {
                ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            }
        }

        public static async Task<T> InvokeAsync<T>(object[] args)
        {
            var context = Resolve(args);

            // Call (protected Task<T> method) NetCoreStackDispatchProxy.InvokeAsync<T>()
            T returnValue = default(T);
            try
            {
                Debug.Assert(s_dispatchProxyInvokeAsyncTMethod != null);
                var genericmethod = s_dispatchProxyInvokeAsyncTMethod.MakeGenericMethod(typeof(T));
                returnValue = await (Task<T>)genericmethod.Invoke(context.Packed.DispatchProxy,
                                                                       new object[] { context.Method, context.Packed.Args });
                context.Packed.ReturnValue = returnValue;
            }
            catch (TargetInvocationException tie)
            {
                ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            }
            return returnValue;
        }

        private class PackedArgs
        {
            internal const int DispatchProxyPosition = 0;
            internal const int DeclaringTypePosition = 1;
            internal const int MethodTokenPosition = 2;
            internal const int ArgsPosition = 3;
            internal const int GenericTypesPosition = 4;
            internal const int ReturnValuePosition = 5;

            internal static readonly Type[] PackedTypes = new Type[] { typeof(object), typeof(Type), typeof(int), typeof(object[]), typeof(Type[]), typeof(object) };

            private object[] _args;
            internal PackedArgs() : this(new object[PackedTypes.Length]) { }
            internal PackedArgs(object[] args) { _args = args; }

            internal DispatchProxyAsync DispatchProxy { get { return (DispatchProxyAsync)_args[DispatchProxyPosition]; } }
            internal Type DeclaringType { get { return (Type)_args[DeclaringTypePosition]; } }
            internal int MethodToken { get { return (int)_args[MethodTokenPosition]; } }
            internal object[] Args { get { return (object[])_args[ArgsPosition]; } }
            internal Type[] GenericTypes { get { return (Type[])_args[GenericTypesPosition]; } }
            internal object ReturnValue { /*get { return args[ReturnValuePosition]; }*/ set { _args[ReturnValuePosition] = value; } }
        }

        private class ProxyAssembly
        {
            public AssemblyBuilder _ab;
            private ModuleBuilder _mb;
            private int _typeId = 0;

            // Maintain a MethodBase-->int, int-->MethodBase mapping to permit generated code
            // to pass methods by token
            private Dictionary<MethodBase, int> _methodToToken = new Dictionary<MethodBase, int>();
            private List<MethodBase> _methodsByToken = new List<MethodBase>();
            private HashSet<string> _ignoresAccessAssemblyNames = new HashSet<string>();
            private ConstructorInfo _ignoresAccessChecksToAttributeConstructor;

            public ProxyAssembly()
            {
                AssemblyBuilderAccess access = AssemblyBuilderAccess.Run;
                var assemblyName = new AssemblyName("ProxyBuilder2");
                assemblyName.Version = new Version(1, 0, 0);
                _ab = AssemblyBuilder.DefineDynamicAssembly(assemblyName, access);
                _mb = _ab.DefineDynamicModule("testmod");
            }

            // Gets or creates the ConstructorInfo for the IgnoresAccessChecksAttribute.
            // This attribute is both defined and referenced in the dynamic assembly to
            // allow access to internal types in other assemblies.
            internal ConstructorInfo IgnoresAccessChecksAttributeConstructor
            {
                get
                {
                    if (_ignoresAccessChecksToAttributeConstructor == null)
                    {
                        TypeInfo attributeTypeInfo = GenerateTypeInfoOfIgnoresAccessChecksToAttribute();
                        _ignoresAccessChecksToAttributeConstructor = attributeTypeInfo.DeclaredConstructors.Single();
                    }

                    return _ignoresAccessChecksToAttributeConstructor;
                }
            }
            public ProxyBuilder CreateProxy(string name, Type proxyBaseType)
            {
                int nextId = Interlocked.Increment(ref _typeId);
                TypeBuilder tb = _mb.DefineType(name + "_" + nextId, TypeAttributes.Public, proxyBaseType);
                return new ProxyBuilder(this, tb, proxyBaseType);
            }

            // Generate the declaration for the IgnoresAccessChecksToAttribute type.
            // This attribute will be both defined and used in the dynamic assembly.
            // Each usage identifies the name of the assembly containing non-public
            // types the dynamic assembly needs to access.  Normally those types
            // would be inaccessible, but this attribute allows them to be visible.
            // It works like a reverse InternalsVisibleToAttribute.
            // This method returns the TypeInfo of the generated attribute.
            private TypeInfo GenerateTypeInfoOfIgnoresAccessChecksToAttribute()
            {
                TypeBuilder attributeTypeBuilder =
                    _mb.DefineType("System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute",
                                   TypeAttributes.Public | TypeAttributes.Class,
                                   typeof(Attribute));

                // Create backing field as:
                // private string assemblyName;
                FieldBuilder assemblyNameField =
                    attributeTypeBuilder.DefineField("assemblyName", typeof(String), FieldAttributes.Private);

                // Create ctor as:
                // public IgnoresAccessChecksToAttribute(string)
                ConstructorBuilder constructorBuilder = attributeTypeBuilder.DefineConstructor(MethodAttributes.Public,
                                                             CallingConventions.HasThis,
                                                             new Type[] { assemblyNameField.FieldType });

                ILGenerator il = constructorBuilder.GetILGenerator();

                // Create ctor body as:
                // this.assemblyName = {ctor parameter 0}
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg, 1);
                il.Emit(OpCodes.Stfld, assemblyNameField);

                // return
                il.Emit(OpCodes.Ret);

                // Define property as:
                // public string AssemblyName {get { return this.assemblyName; } }
                PropertyBuilder getterPropertyBuilder = attributeTypeBuilder.DefineProperty(
                                                       "AssemblyName",
                                                       PropertyAttributes.None,
                                                       CallingConventions.HasThis,
                                                       returnType: typeof(String),
                                                       parameterTypes: null);

                MethodBuilder getterMethodBuilder = attributeTypeBuilder.DefineMethod(
                                                       "get_AssemblyName",
                                                       MethodAttributes.Public,
                                                       CallingConventions.HasThis,
                                                       returnType: typeof(String),
                                                       parameterTypes: null);

                // Generate body:
                // return this.assemblyName;
                il = getterMethodBuilder.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, assemblyNameField);
                il.Emit(OpCodes.Ret);

                // Generate the AttributeUsage attribute for this attribute type:
                // [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
                TypeInfo attributeUsageTypeInfo = typeof(AttributeUsageAttribute).GetTypeInfo();

                // Find the ctor that takes only AttributeTargets
                ConstructorInfo attributeUsageConstructorInfo =
                    attributeUsageTypeInfo.DeclaredConstructors
                        .Single(c => c.GetParameters().Count() == 1 &&
                                     c.GetParameters()[0].ParameterType == typeof(AttributeTargets));

                // Find the property to set AllowMultiple
                PropertyInfo allowMultipleProperty =
                    attributeUsageTypeInfo.DeclaredProperties
                        .Single(f => String.Equals(f.Name, "AllowMultiple"));

                // Create a builder to construct the instance via the ctor and property
                CustomAttributeBuilder customAttributeBuilder =
                    new CustomAttributeBuilder(attributeUsageConstructorInfo,
                                                new object[] { AttributeTargets.Assembly },
                                                new PropertyInfo[] { allowMultipleProperty },
                                                new object[] { true });

                // Attach this attribute instance to the newly defined attribute type
                attributeTypeBuilder.SetCustomAttribute(customAttributeBuilder);

                // Make the TypeInfo real so the constructor can be used.
                return attributeTypeBuilder.CreateTypeInfo();
            }

            // Generates an instance of the IgnoresAccessChecksToAttribute to
            // identify the given assembly as one which contains internal types
            // the dynamic assembly will need to reference.
            internal void GenerateInstanceOfIgnoresAccessChecksToAttribute(string assemblyName)
            {
                // Add this assembly level attribute:
                // [assembly: System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute(assemblyName)]
                ConstructorInfo attributeConstructor = IgnoresAccessChecksAttributeConstructor;
                CustomAttributeBuilder customAttributeBuilder =
                    new CustomAttributeBuilder(attributeConstructor, new object[] { assemblyName });
                _ab.SetCustomAttribute(customAttributeBuilder);
            }

            // Ensures the type we will reference from the dynamic assembly
            // is visible.  Non-public types need to emit an attribute that
            // allows access from the dynamic assembly.
            internal void EnsureTypeIsVisible(Type type)
            {
                TypeInfo typeInfo = type.GetTypeInfo();
                if (!typeInfo.IsVisible)
                {
                    string assemblyName = typeInfo.Assembly.GetName().Name;
                    if (!_ignoresAccessAssemblyNames.Contains(assemblyName))
                    {
                        GenerateInstanceOfIgnoresAccessChecksToAttribute(assemblyName);
                        _ignoresAccessAssemblyNames.Add(assemblyName);
                    }
                }
            }

            internal void GetTokenForMethod(MethodBase method, out Type type, out int token)
            {
                type = method.DeclaringType;
                token = 0;
                if (!_methodToToken.TryGetValue(method, out token))
                {
                    _methodsByToken.Add(method);
                    token = _methodsByToken.Count - 1;
                    _methodToToken[method] = token;
                }
            }

            internal MethodBase ResolveMethodToken(Type type, int token)
            {
                Debug.Assert(token >= 0 && token < _methodsByToken.Count);
                return _methodsByToken[token];
            }
        }

        private class ProxyBuilder
        {
            private static readonly MethodInfo s_delegateInvoke = typeof(DispatchProxyHandler).GetMethod("InvokeHandle");
            private static readonly MethodInfo s_delegateInvokeAsync = typeof(DispatchProxyHandler).GetMethod("InvokeAsyncHandle");
            private static readonly MethodInfo s_delegateinvokeAsyncT = typeof(DispatchProxyHandler).GetMethod("InvokeAsyncHandleT");

            private ProxyAssembly _assembly;
            private TypeBuilder _tb;
            private Type _proxyBaseType;
            private List<FieldBuilder> _fields;

            internal ProxyBuilder(ProxyAssembly assembly, TypeBuilder tb, Type proxyBaseType)
            {
                _assembly = assembly;
                _tb = tb;
                _proxyBaseType = proxyBaseType;

                _fields = new List<FieldBuilder>();
                _fields.Add(tb.DefineField("_handler", typeof(DispatchProxyHandler), FieldAttributes.Private));
            }

            private static bool IsGenericTask(Type type)
            {
                var current = type;
                while (current != null)
                {
                    if (current.GetTypeInfo().IsGenericType && current.GetGenericTypeDefinition() == typeof(Task<>))
                        return true;
                    current = current.GetTypeInfo().BaseType;
                }
                return false;
            }

            private void Complete()
            {
                Type[] args = new Type[_fields.Count];
                for (int i = 0; i < args.Length; i++)
                {
                    args[i] = _fields[i].FieldType;
                }

                ConstructorBuilder cb = _tb.DefineConstructor(MethodAttributes.Public, CallingConventions.HasThis, args);
                ILGenerator il = cb.GetILGenerator();

                // chained ctor call
                ConstructorInfo baseCtor = _proxyBaseType.GetTypeInfo().DeclaredConstructors.SingleOrDefault(c => c.IsPublic && c.GetParameters().Length == 0);
                Debug.Assert(baseCtor != null);

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, baseCtor);

                // store all the fields
                for (int i = 0; i < args.Length; i++)
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg, i + 1);
                    il.Emit(OpCodes.Stfld, _fields[i]);
                }

                il.Emit(OpCodes.Ret);
            }

            internal Type CreateType()
            {
                this.Complete();
                return _tb.CreateTypeInfo().AsType();
            }

            internal void AddInterfaceImpl(Type iface)
            {
                // If necessary, generate an attribute to permit visibility
                // to internal types.
                _assembly.EnsureTypeIsVisible(iface);

                _tb.AddInterfaceImplementation(iface);

                // AccessorMethods -> Metadata mappings.
                var propertyMap = new Dictionary<MethodInfo, PropertyAccessorInfo>(MethodInfoEqualityComparer.Instance);
                foreach (PropertyInfo pi in iface.GetRuntimeProperties())
                {
                    var ai = new PropertyAccessorInfo(pi.GetMethod, pi.SetMethod);
                    if (pi.GetMethod != null)
                        propertyMap[pi.GetMethod] = ai;
                    if (pi.SetMethod != null)
                        propertyMap[pi.SetMethod] = ai;
                }

                var eventMap = new Dictionary<MethodInfo, EventAccessorInfo>(MethodInfoEqualityComparer.Instance);
                foreach (EventInfo ei in iface.GetRuntimeEvents())
                {
                    var ai = new EventAccessorInfo(ei.AddMethod, ei.RemoveMethod, ei.RaiseMethod);
                    if (ei.AddMethod != null)
                        eventMap[ei.AddMethod] = ai;
                    if (ei.RemoveMethod != null)
                        eventMap[ei.RemoveMethod] = ai;
                    if (ei.RaiseMethod != null)
                        eventMap[ei.RaiseMethod] = ai;
                }

                foreach (MethodInfo mi in iface.GetRuntimeMethods())
                {
                    MethodBuilder mdb = AddMethodImpl(mi);
                    PropertyAccessorInfo associatedProperty;
                    if (propertyMap.TryGetValue(mi, out associatedProperty))
                    {
                        if (MethodInfoEqualityComparer.Instance.Equals(associatedProperty.InterfaceGetMethod, mi))
                            associatedProperty.GetMethodBuilder = mdb;
                        else
                            associatedProperty.SetMethodBuilder = mdb;
                    }

                    EventAccessorInfo associatedEvent;
                    if (eventMap.TryGetValue(mi, out associatedEvent))
                    {
                        if (MethodInfoEqualityComparer.Instance.Equals(associatedEvent.InterfaceAddMethod, mi))
                            associatedEvent.AddMethodBuilder = mdb;
                        else if (MethodInfoEqualityComparer.Instance.Equals(associatedEvent.InterfaceRemoveMethod, mi))
                            associatedEvent.RemoveMethodBuilder = mdb;
                        else
                            associatedEvent.RaiseMethodBuilder = mdb;
                    }
                }

                foreach (PropertyInfo pi in iface.GetRuntimeProperties())
                {
                    PropertyAccessorInfo ai = propertyMap[pi.GetMethod ?? pi.SetMethod];
                    PropertyBuilder pb = _tb.DefineProperty(pi.Name, pi.Attributes, pi.PropertyType, pi.GetIndexParameters().Select(p => p.ParameterType).ToArray());
                    if (ai.GetMethodBuilder != null)
                        pb.SetGetMethod(ai.GetMethodBuilder);
                    if (ai.SetMethodBuilder != null)
                        pb.SetSetMethod(ai.SetMethodBuilder);
                }

                foreach (EventInfo ei in iface.GetRuntimeEvents())
                {
                    EventAccessorInfo ai = eventMap[ei.AddMethod ?? ei.RemoveMethod];
                    EventBuilder eb = _tb.DefineEvent(ei.Name, ei.Attributes, ei.EventHandlerType);
                    if (ai.AddMethodBuilder != null)
                        eb.SetAddOnMethod(ai.AddMethodBuilder);
                    if (ai.RemoveMethodBuilder != null)
                        eb.SetRemoveOnMethod(ai.RemoveMethodBuilder);
                    if (ai.RaiseMethodBuilder != null)
                        eb.SetRaiseMethod(ai.RaiseMethodBuilder);
                }
            }

            private MethodBuilder AddMethodImpl(MethodInfo mi)
            {
                ParameterInfo[] parameters = mi.GetParameters();
                Type[] paramTypes = ParamTypes(parameters, false);

                MethodBuilder mdb = _tb.DefineMethod(mi.Name, MethodAttributes.Public | MethodAttributes.Virtual, mi.ReturnType, paramTypes);
                if (mi.ContainsGenericParameters)
                {
                    Type[] ts = mi.GetGenericArguments();
                    string[] ss = new string[ts.Length];
                    for (int i = 0; i < ts.Length; i++)
                    {
                        ss[i] = ts[i].Name;
                    }
                    GenericTypeParameterBuilder[] genericParameters = mdb.DefineGenericParameters(ss);
                    for (int i = 0; i < genericParameters.Length; i++)
                    {
                        genericParameters[i].SetGenericParameterAttributes(ts[i].GetTypeInfo().GenericParameterAttributes);
                    }
                }
                ILGenerator il = mdb.GetILGenerator();

                ParametersArray args = new ParametersArray(il, paramTypes);

                // object[] args = new object[paramCount];
                il.Emit(OpCodes.Nop);
                GenericArray<object> argsArr = new GenericArray<object>(il, ParamTypes(parameters, true).Length);

                for (int i = 0; i < parameters.Length; i++)
                {
                    // args[i] = argi;
                    if (!parameters[i].IsOut)
                    {
                        argsArr.BeginSet(i);
                        args.Get(i);
                        argsArr.EndSet(parameters[i].ParameterType);
                    }
                }

                // object[] packed = new object[PackedArgs.PackedTypes.Length];
                GenericArray<object> packedArr = new GenericArray<object>(il, PackedArgs.PackedTypes.Length);

                // packed[PackedArgs.DispatchProxyPosition] = this;
                packedArr.BeginSet(PackedArgs.DispatchProxyPosition);
                il.Emit(OpCodes.Ldarg_0);
                packedArr.EndSet(typeof(DispatchProxyAsync));

                // packed[PackedArgs.DeclaringTypePosition] = typeof(iface);
                MethodInfo Type_GetTypeFromHandle = typeof(Type).GetRuntimeMethod("GetTypeFromHandle", new Type[] { typeof(RuntimeTypeHandle) });
                int methodToken;
                Type declaringType;
                _assembly.GetTokenForMethod(mi, out declaringType, out methodToken);
                packedArr.BeginSet(PackedArgs.DeclaringTypePosition);
                il.Emit(OpCodes.Ldtoken, declaringType);
                il.Emit(OpCodes.Call, Type_GetTypeFromHandle);
                packedArr.EndSet(typeof(object));

                // packed[PackedArgs.MethodTokenPosition] = iface method token;
                packedArr.BeginSet(PackedArgs.MethodTokenPosition);
                il.Emit(OpCodes.Ldc_I4, methodToken);
                packedArr.EndSet(typeof(Int32));

                // packed[PackedArgs.ArgsPosition] = args;
                packedArr.BeginSet(PackedArgs.ArgsPosition);
                argsArr.Load();
                packedArr.EndSet(typeof(object[]));

                // packed[PackedArgs.GenericTypesPosition] = mi.GetGenericArguments();
                if (mi.ContainsGenericParameters)
                {
                    packedArr.BeginSet(PackedArgs.GenericTypesPosition);
                    Type[] genericTypes = mi.GetGenericArguments();
                    GenericArray<Type> typeArr = new GenericArray<Type>(il, genericTypes.Length);
                    for (int i = 0; i < genericTypes.Length; ++i)
                    {
                        typeArr.BeginSet(i);
                        il.Emit(OpCodes.Ldtoken, genericTypes[i]);
                        il.Emit(OpCodes.Call, Type_GetTypeFromHandle);
                        typeArr.EndSet(typeof(Type));
                    }
                    typeArr.Load();
                    packedArr.EndSet(typeof(Type[]));
                }

                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].ParameterType.IsByRef)
                    {
                        args.BeginSet(i);
                        argsArr.Get(i);
                        args.EndSet(i, typeof(object));
                    }
                }

                MethodInfo invokeMethod = s_delegateInvoke;
                if (mi.ReturnType == typeof(Task))
                {
                    invokeMethod = s_delegateInvokeAsync;
                }
                if (IsGenericTask(mi.ReturnType))
                {
                    var returnTypes = mi.ReturnType.GetGenericArguments();
                    invokeMethod = s_delegateinvokeAsyncT.MakeGenericMethod(returnTypes);
                }

                // Call AsyncDispatchProxyGenerator.Invoke(object[]), InvokeAsync or InvokeAsyncT
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, _fields[InvokeActionFieldAndCtorParameterIndex]);
                packedArr.Load();
                il.Emit(OpCodes.Callvirt, invokeMethod);
                if (mi.ReturnType != typeof(void))
                {
                    Convert(il, typeof(object), mi.ReturnType, false);
                }
                else
                {
                    il.Emit(OpCodes.Pop);
                }

                il.Emit(OpCodes.Ret);

                _tb.DefineMethodOverride(mdb, mi);
                return mdb;
            }

            private static Type[] ParamTypes(ParameterInfo[] parms, bool noByRef)
            {
                Type[] types = new Type[parms.Length];
                for (int i = 0; i < parms.Length; i++)
                {
                    types[i] = parms[i].ParameterType;
                    if (noByRef && types[i].IsByRef)
                        types[i] = types[i].GetElementType();
                }
                return types;
            }

            // TypeCode does not exist in ProjectK or ProjectN.
            // This lookup method was copied from PortableLibraryThunks\Internal\PortableLibraryThunks\System\TypeThunks.cs
            // but returns the integer value equivalent to its TypeCode enum.
            private static int GetTypeCode(Type type)
            {
                if (type == null)
                    return 0;   // TypeCode.Empty;

                if (type == typeof(Boolean))
                    return 3;   // TypeCode.Boolean;

                if (type == typeof(Char))
                    return 4;   // TypeCode.Char;

                if (type == typeof(SByte))
                    return 5;   // TypeCode.SByte;

                if (type == typeof(Byte))
                    return 6;   // TypeCode.Byte;

                if (type == typeof(Int16))
                    return 7;   // TypeCode.Int16;

                if (type == typeof(UInt16))
                    return 8;   // TypeCode.UInt16;

                if (type == typeof(Int32))
                    return 9;   // TypeCode.Int32;

                if (type == typeof(UInt32))
                    return 10;  // TypeCode.UInt32;

                if (type == typeof(Int64))
                    return 11;  // TypeCode.Int64;

                if (type == typeof(UInt64))
                    return 12;  // TypeCode.UInt64;

                if (type == typeof(Single))
                    return 13;  // TypeCode.Single;

                if (type == typeof(Double))
                    return 14;  // TypeCode.Double;

                if (type == typeof(Decimal))
                    return 15;  // TypeCode.Decimal;

                if (type == typeof(DateTime))
                    return 16;  // TypeCode.DateTime;

                if (type == typeof(String))
                    return 18;  // TypeCode.String;

                if (type.GetTypeInfo().IsEnum)
                    return GetTypeCode(Enum.GetUnderlyingType(type));

                return 1;   // TypeCode.Object;
            }

            private static OpCode[] s_convOpCodes = new OpCode[] {
                OpCodes.Nop,//Empty = 0,
                OpCodes.Nop,//Object = 1,
                OpCodes.Nop,//DBNull = 2,
                OpCodes.Conv_I1,//Boolean = 3,
                OpCodes.Conv_I2,//Char = 4,
                OpCodes.Conv_I1,//SByte = 5,
                OpCodes.Conv_U1,//Byte = 6,
                OpCodes.Conv_I2,//Int16 = 7,
                OpCodes.Conv_U2,//UInt16 = 8,
                OpCodes.Conv_I4,//Int32 = 9,
                OpCodes.Conv_U4,//UInt32 = 10,
                OpCodes.Conv_I8,//Int64 = 11,
                OpCodes.Conv_U8,//UInt64 = 12,
                OpCodes.Conv_R4,//Single = 13,
                OpCodes.Conv_R8,//Double = 14,
                OpCodes.Nop,//Decimal = 15,
                OpCodes.Nop,//DateTime = 16,
                OpCodes.Nop,//17
                OpCodes.Nop,//String = 18,
            };

            private static OpCode[] s_ldindOpCodes = new OpCode[] {
                OpCodes.Nop,//Empty = 0,
                OpCodes.Nop,//Object = 1,
                OpCodes.Nop,//DBNull = 2,
                OpCodes.Ldind_I1,//Boolean = 3,
                OpCodes.Ldind_I2,//Char = 4,
                OpCodes.Ldind_I1,//SByte = 5,
                OpCodes.Ldind_U1,//Byte = 6,
                OpCodes.Ldind_I2,//Int16 = 7,
                OpCodes.Ldind_U2,//UInt16 = 8,
                OpCodes.Ldind_I4,//Int32 = 9,
                OpCodes.Ldind_U4,//UInt32 = 10,
                OpCodes.Ldind_I8,//Int64 = 11,
                OpCodes.Ldind_I8,//UInt64 = 12,
                OpCodes.Ldind_R4,//Single = 13,
                OpCodes.Ldind_R8,//Double = 14,
                OpCodes.Nop,//Decimal = 15,
                OpCodes.Nop,//DateTime = 16,
                OpCodes.Nop,//17
                OpCodes.Ldind_Ref,//String = 18,
            };

            private static OpCode[] s_stindOpCodes = new OpCode[] {
                OpCodes.Nop,//Empty = 0,
                OpCodes.Nop,//Object = 1,
                OpCodes.Nop,//DBNull = 2,
                OpCodes.Stind_I1,//Boolean = 3,
                OpCodes.Stind_I2,//Char = 4,
                OpCodes.Stind_I1,//SByte = 5,
                OpCodes.Stind_I1,//Byte = 6,
                OpCodes.Stind_I2,//Int16 = 7,
                OpCodes.Stind_I2,//UInt16 = 8,
                OpCodes.Stind_I4,//Int32 = 9,
                OpCodes.Stind_I4,//UInt32 = 10,
                OpCodes.Stind_I8,//Int64 = 11,
                OpCodes.Stind_I8,//UInt64 = 12,
                OpCodes.Stind_R4,//Single = 13,
                OpCodes.Stind_R8,//Double = 14,
                OpCodes.Nop,//Decimal = 15,
                OpCodes.Nop,//DateTime = 16,
                OpCodes.Nop,//17
                OpCodes.Stind_Ref,//String = 18,
            };

            private static void Convert(ILGenerator il, Type source, Type target, bool isAddress)
            {
                Debug.Assert(!target.IsByRef);
                if (target == source)
                    return;

                TypeInfo sourceTypeInfo = source.GetTypeInfo();
                TypeInfo targetTypeInfo = target.GetTypeInfo();

                if (source.IsByRef)
                {
                    Debug.Assert(!isAddress);
                    Type argType = source.GetElementType();
                    Ldind(il, argType);
                    Convert(il, argType, target, isAddress);
                    return;
                }
                if (targetTypeInfo.IsValueType)
                {
                    if (sourceTypeInfo.IsValueType)
                    {
                        OpCode opCode = s_convOpCodes[GetTypeCode(target)];
                        Debug.Assert(!opCode.Equals(OpCodes.Nop));
                        il.Emit(opCode);
                    }
                    else
                    {
                        Debug.Assert(sourceTypeInfo.IsAssignableFrom(targetTypeInfo));
                        il.Emit(OpCodes.Unbox, target);
                        if (!isAddress)
                            Ldind(il, target);
                    }
                }
                else if (targetTypeInfo.IsAssignableFrom(sourceTypeInfo))
                {
                    if (sourceTypeInfo.IsValueType)
                    {
                        if (isAddress)
                            Ldind(il, source);
                        il.Emit(OpCodes.Box, source);
                    }
                }
                else
                {
                    Debug.Assert(sourceTypeInfo.IsAssignableFrom(targetTypeInfo) || targetTypeInfo.IsInterface || sourceTypeInfo.IsInterface);
                    if (target.IsGenericParameter)
                    {
                        // T GetProperty<T>() where T : class;
                        Debug.Assert(targetTypeInfo.GenericParameterAttributes == GenericParameterAttributes.ReferenceTypeConstraint);
                        il.Emit(OpCodes.Unbox_Any, target);
                    }
                    else
                    {
                        il.Emit(OpCodes.Castclass, target);
                    }
                }
            }

            private static void Ldind(ILGenerator il, Type type)
            {
                OpCode opCode = s_ldindOpCodes[GetTypeCode(type)];
                if (!opCode.Equals(OpCodes.Nop))
                {
                    il.Emit(opCode);
                }
                else
                {
                    il.Emit(OpCodes.Ldobj, type);
                }
            }

            private static void Stind(ILGenerator il, Type type)
            {
                OpCode opCode = s_stindOpCodes[GetTypeCode(type)];
                if (!opCode.Equals(OpCodes.Nop))
                {
                    il.Emit(opCode);
                }
                else
                {
                    il.Emit(OpCodes.Stobj, type);
                }
            }

            private class ParametersArray
            {
                private ILGenerator _il;
                private Type[] _paramTypes;
                internal ParametersArray(ILGenerator il, Type[] paramTypes)
                {
                    _il = il;
                    _paramTypes = paramTypes;
                }

                internal void Get(int i)
                {
                    _il.Emit(OpCodes.Ldarg, i + 1);
                }

                internal void BeginSet(int i)
                {
                    _il.Emit(OpCodes.Ldarg, i + 1);
                }

                internal void EndSet(int i, Type stackType)
                {
                    Debug.Assert(_paramTypes[i].IsByRef);
                    Type argType = _paramTypes[i].GetElementType();
                    Convert(_il, stackType, argType, false);
                    Stind(_il, argType);
                }
            }

            private class GenericArray<T>
            {
                private ILGenerator _il;
                private LocalBuilder _lb;
                internal GenericArray(ILGenerator il, int len)
                {
                    _il = il;
                    _lb = il.DeclareLocal(typeof(T[]));

                    il.Emit(OpCodes.Ldc_I4, len);
                    il.Emit(OpCodes.Newarr, typeof(T));
                    il.Emit(OpCodes.Stloc, _lb);
                }

                internal void Load()
                {
                    _il.Emit(OpCodes.Ldloc, _lb);
                }

                internal void Get(int i)
                {
                    _il.Emit(OpCodes.Ldloc, _lb);
                    _il.Emit(OpCodes.Ldc_I4, i);
                    _il.Emit(OpCodes.Ldelem_Ref);
                }

                internal void BeginSet(int i)
                {
                    _il.Emit(OpCodes.Ldloc, _lb);
                    _il.Emit(OpCodes.Ldc_I4, i);
                }

                internal void EndSet(Type stackType)
                {
                    Convert(_il, stackType, typeof(T), false);
                    _il.Emit(OpCodes.Stelem_Ref);
                }
            }

            private sealed class PropertyAccessorInfo
            {
                public MethodInfo InterfaceGetMethod { get; }
                public MethodInfo InterfaceSetMethod { get; }
                public MethodBuilder GetMethodBuilder { get; set; }
                public MethodBuilder SetMethodBuilder { get; set; }

                public PropertyAccessorInfo(MethodInfo interfaceGetMethod, MethodInfo interfaceSetMethod)
                {
                    InterfaceGetMethod = interfaceGetMethod;
                    InterfaceSetMethod = interfaceSetMethod;
                }
            }

            private sealed class EventAccessorInfo
            {
                public MethodInfo InterfaceAddMethod { get; }
                public MethodInfo InterfaceRemoveMethod { get; }
                public MethodInfo InterfaceRaiseMethod { get; }
                public MethodBuilder AddMethodBuilder { get; set; }
                public MethodBuilder RemoveMethodBuilder { get; set; }
                public MethodBuilder RaiseMethodBuilder { get; set; }

                public EventAccessorInfo(MethodInfo interfaceAddMethod, MethodInfo interfaceRemoveMethod, MethodInfo interfaceRaiseMethod)
                {
                    InterfaceAddMethod = interfaceAddMethod;
                    InterfaceRemoveMethod = interfaceRemoveMethod;
                    InterfaceRaiseMethod = interfaceRaiseMethod;
                }
            }

            private sealed class MethodInfoEqualityComparer : EqualityComparer<MethodInfo>
            {
                public static readonly MethodInfoEqualityComparer Instance = new MethodInfoEqualityComparer();

                private MethodInfoEqualityComparer() { }

                public sealed override bool Equals(MethodInfo left, MethodInfo right)
                {
                    if (ReferenceEquals(left, right))
                        return true;

                    if (left == null)
                        return right == null;
                    else if (right == null)
                        return false;

                    // This assembly should work in netstandard1.3,
                    // so we cannot use MemberInfo.MetadataToken here.
                    // Therefore, it compares honestly referring ECMA-335 I.8.6.1.6 Signature Matching.
                    if (!Equals(left.DeclaringType, right.DeclaringType))
                        return false;

                    if (!Equals(left.ReturnType, right.ReturnType))
                        return false;

                    if (left.CallingConvention != right.CallingConvention)
                        return false;

                    if (left.IsStatic != right.IsStatic)
                        return false;

                    if (left.Name != right.Name)
                        return false;

                    Type[] leftGenericParameters = left.GetGenericArguments();
                    Type[] rightGenericParameters = right.GetGenericArguments();
                    if (leftGenericParameters.Length != rightGenericParameters.Length)
                        return false;

                    for (int i = 0; i < leftGenericParameters.Length; i++)
                    {
                        if (!Equals(leftGenericParameters[i], rightGenericParameters[i]))
                            return false;
                    }

                    ParameterInfo[] leftParameters = left.GetParameters();
                    ParameterInfo[] rightParameters = right.GetParameters();
                    if (leftParameters.Length != rightParameters.Length)
                        return false;

                    for (int i = 0; i < leftParameters.Length; i++)
                    {
                        if (!Equals(leftParameters[i].ParameterType, rightParameters[i].ParameterType))
                            return false;
                    }

                    return true;
                }

                public sealed override int GetHashCode(MethodInfo obj)
                {
                    if (obj == null)
                        return 0;

                    int hashCode = obj.DeclaringType.GetHashCode();
                    hashCode ^= obj.Name.GetHashCode();
                    foreach (ParameterInfo parameter in obj.GetParameters())
                    {
                        hashCode ^= parameter.ParameterType.GetHashCode();
                    }

                    return hashCode;
                }
            }
        }
    }
}
