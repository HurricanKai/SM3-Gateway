using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Proxy
{
    public class MCConnectionHandler : ConnectionHandler
    {
        private readonly ILogger<MCConnectionHandler> _logger;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly string _hostname;
        private readonly int _port;

        public MCConnectionHandler(ILogger<MCConnectionHandler> logger, IConfiguration config)
        {
            _hostname = config["hostname"];
            _port = int.Parse(config["port"]);
            _logger = logger;
            _jsonOptions = new JsonSerializerOptions()
            {
                IgnoreNullValues = true
            };
        }

        public override async Task OnConnectedAsync(ConnectionContext connection)
        {
            var ctx = new MConnectionContext(connection);
            try
            {
                await Handle(ctx);
            }
            catch (Exception e)
            {
                if (ctx.ConnectionClosed.IsCancellationRequested || ctx.LocalEndPoint == null || ctx.RemoteEndPoint == null)
                { }
                else
                {
                    throw;
                }
            }
        }
        
        private async Task Handle(MConnectionContext ctx)
        {
            ReadHandshake(ctx);

            if (ctx.ConnectionClosed.IsCancellationRequested)
                return;

            _logger.LogInformation(
                $"Received handshake, {ctx.ProtocolVersion}, {ctx.ServerAddress}:{ctx.Port}");

            bool isBackendUp = true;
            using var backendClient = new TcpClient();
            backendClient.NoDelay = true;
            var connectAttempt = backendClient.BeginConnect(_hostname, _port, null, null);

            isBackendUp = connectAttempt.AsyncWaitHandle.WaitOne(100);

            if (isBackendUp)
                backendClient.EndConnect(connectAttempt);

                if (ctx.Stage == 1 && !isBackendUp)
            {
                ctx.Items["flush"] = false;
                ctx.Items["close"] = false;
                while (!ctx.ConnectionClosed.IsCancellationRequested)
                {
                    ReadResult readResult;
                    try
                    {
                        readResult = await ctx.Transport.Input.ReadAsync();
                    }
                    catch (Exception e)
                    {
                        return;
                    }
                    if (readResult.IsCanceled || readResult.IsCompleted)
                    {
                        _logger.LogInformation("Connection Closed");
                        return;
                    }
                
                    var buffer = readResult.Buffer;
                    HandlePacket(buffer, ctx);

                    if ((bool)ctx.Items["flush"])
                    {
                        await ctx.Transport.Output.FlushAsync();
                        ctx.Items["flush"] = false;
                    }

                    if ((bool)ctx.Items["close"] /* we don't specifically close, we just hand it back to kestrel to deal with */)
                        return;
                }
            }

            await using var backendStream = backendClient.GetStream();

            WriteHandshake(backendStream, ctx.ProtocolVersion, ctx.ServerAddress, ctx.Port, ctx.Stage);

            var reader = PipeReader.Create(backendStream);
            var writer = PipeWriter.Create(backendStream);

            await Task.WhenAny(ctx.Transport.Input.CopyToAsync(writer),
                               reader.CopyToAsync(ctx.Transport.Output));
            await reader.CompleteAsync();
            await writer.CompleteAsync();
        }
        
        private void HandlePacket(in ReadOnlySequence<byte> buffer, ConnectionContext ctx)
        {
            var reader = new MCPacketReader(buffer);
            MCPacketWriter writer;
            var length = reader.ReadVarInt();

            if (length > reader.Buffer.Length || length < 1 /* 1 = small ID but no fields*/)
            {
                if (length == 0xFE)
                    _logger.LogInformation("Legacy Ping");
                else
                    _logger.LogCritical($"Read Invalid length {length:X}. Aborting");

                ctx.Abort();
                return;
            }
            
            reader = new MCPacketReader(reader.Buffer.Slice(0, length));
            var id = reader.ReadVarInt();
            using var packetIdScope = _logger.BeginScope($"Packet ID: {id:x2}");
            switch (id)
            {
                case 0x00: // Status Request
                    var payload = new StatusResponse(
                        new StatusResponse.VersionPayload("SM3-Gateway", 498),
                        new StatusResponse.PlayersPayload(100, 0, null), new ChatBuilder()
                                                                         .AppendText("Backend is Down")
                                                                         .WithColor("red")
                                                                         .Bold()
                                                                         .Build(),
                        null);
                    var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions);
                    var dataSize1 = payloadBytes.Length + MCPacketWriter.GetVarIntSize(payloadBytes.Length) +
                                    MCPacketWriter.GetVarIntSize(0x00);
                    var packetSize1 = dataSize1 + MCPacketWriter.GetVarIntSize(dataSize1);
                    var span = ctx.Transport.Output.GetSpan(packetSize1);
                    writer = new MCPacketWriter(span, MemoryPool<byte>.Shared);
                    writer.WriteVarInt(dataSize1);
                    writer.WriteVarInt(0x00);
                    writer.WriteVarInt(payloadBytes.Length);
                    writer.WriteBytes(payloadBytes);
                    ctx.Transport.Output.Advance(packetSize1);
                    ctx.Items["flush"] = true;
                    break;
                case 0x01: // Ping
                    var seed = reader.ReadInt64();

                    var dataSize2 = MCPacketWriter.GetVarIntSize(0x01) + sizeof(long);
                    var packetSize2 = MCPacketWriter.GetVarIntSize(dataSize2) + dataSize2;
                    writer = new MCPacketWriter(ctx.Transport.Output.GetSpan(packetSize2), MemoryPool<byte>.Shared);
                    writer.WriteVarInt(dataSize2);
                    writer.WriteVarInt(0x01);
                    writer.WriteInt64(seed);
                    ctx.Transport.Output.Advance(packetSize2);
                    ctx.Items["flush"] = true;
                    break;
                default:
                    _logger.LogInformation($"Unknown Status Packet {id:x2}");
                    break;
            }

            // NOT IDEAL, but easiest
            ctx.Transport.Input.AdvanceTo(buffer.GetPosition(length + MCPacketWriter.GetVarIntSize(length)));
        }

        private static void WriteHandshake(Stream stream, int protocolVersion, string serverAddress, ushort port, int stage)
        {
            var payloadSize = MCPacketWriter.GetVarIntSize(0x00) + MCPacketWriter.GetVarIntSize(protocolVersion) +
                       MCPacketWriter.GetVarIntSize(serverAddress.Length) + serverAddress.Length + sizeof(ushort) +
                       MCPacketWriter.GetVarIntSize(stage);
            
            Span<byte> data = stackalloc byte[payloadSize + MCPacketWriter.GetVarIntSize(payloadSize)];
            var writer = new MCPacketWriter(data, MemoryPool<byte>.Shared);
            writer.WriteVarInt(payloadSize);
            writer.WriteVarInt(0x00);
            writer.WriteVarInt(protocolVersion);
            writer.WriteString(serverAddress);
            writer.WriteUInt16(port);
            writer.WriteVarInt(stage);

            stream.Write(data);
        }

        private void ReadHandshake(MConnectionContext ctx)
        {
            if (!ctx.Transport.Input.TryRead(out var readResult))
            {
                _logger.LogInformation($"Could not read handshake");
                ctx.Abort();
                return;
            }

            var reader = new MCPacketReader(readResult.Buffer);
            var length = reader.ReadVarInt();
            
            if (length > reader.Buffer.Length || length < 1 /* 1 = small ID but no fields*/)
            {
                if (length == 0xFE)
                    _logger.LogInformation("Legacy Ping");
                else
                    _logger.LogCritical($"Read Invalid length {length:X}. Aborting");
                ctx.Abort();
                return;
            }
            
            reader = new MCPacketReader(reader.Buffer.Slice(0, length));
            var id = reader.ReadVarInt();
            if (id != 0x00)
            {
                _logger.LogCritical($"Received data, but was not handshake. aborting.");
                ctx.Abort();
                return;
            }
            
            ctx.ProtocolVersion = reader.ReadVarInt();
            ctx.ServerAddress = reader.ReadString().ToString();
            ctx.Port = reader.ReadUInt16();
            ctx.Stage = reader.ReadVarInt();

            ctx.Transport.Input.AdvanceTo(readResult.Buffer.GetPosition(MCPacketWriter.GetVarIntSize(length) + length));
        }
    }
}