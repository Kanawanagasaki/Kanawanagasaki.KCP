using Kanawanagasaki.KCP.Sample;

var config = new KcpConfig();
var session = new KcpSession(config);
await AppUi.RunAsync(session);
