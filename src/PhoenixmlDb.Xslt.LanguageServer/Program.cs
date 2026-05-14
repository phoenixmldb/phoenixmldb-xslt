using System;
using PhoenixmlDb.Xslt.LanguageServer;
using StreamJsonRpc;

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();

var formatter = new SystemTextJsonFormatter();
var handler = new HeaderDelimitedMessageHandler(stdout, stdin, formatter);
var server = new XsltLanguageServer();
var rpc = new JsonRpc(handler, server);
server.Rpc = rpc;
rpc.StartListening();
await rpc.Completion;
