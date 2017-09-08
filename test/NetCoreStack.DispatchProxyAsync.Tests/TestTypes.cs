// Demonstrates proxies can be made for internal types
internal interface TestType_InternalInterfaceService
{
    string Echo(string message);
}

// Demonstrates proxies can be made for public types implementing internal interfaces
internal interface TestType_PublicInterfaceService_Implements_Internal : TestType_InternalInterfaceService
{
    string Echo2(string message);
}
