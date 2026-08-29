using MiniVNC.Tests;

// MiniVNC 测试套件入口。全部通过返回 0，有失败返回 1（供 CI 判定）。
Console.WriteLine("MiniVNC 测试套件");

HostAddressTests.Run();
FramebufferTests.Run();
AuthTests.Run();
await WireFormatTests.RunAsync();
await StreamTests.RunAsync();
await DecodingTests.RunAsync();
await SessionTests.RunAsync();

return TestRunner.Summarize();
