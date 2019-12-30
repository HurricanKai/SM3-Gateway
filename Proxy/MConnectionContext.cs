using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;

namespace Proxy
{
    public sealed class MConnectionContext : ConnectionContext
    {
        private readonly ConnectionContext _base;

        public MConnectionContext(ConnectionContext @base)
        {
            _base = @base;
        }

        public int ProtocolVersion
        {
            get => (int) Items["pv"];
            set => Items["pv"] = value;
        }
        public string ServerAddress
        {
            get => (string) Items["sa"];
            set => Items["sa"] = value;
        }
        public ushort Port
        {
            get => (ushort) Items["port"];
            set => Items["port"] = value;
        }
        public int Stage
        {
            get => (int) Items["stage"];
            set => Items["stage"] = value;
        }
        private CancellationToken _connectionClosed;
        private EndPoint _localEndPoint;
        private EndPoint _remoteEndPoint;

        public override string ConnectionId
        {
            get => _base.ConnectionId;
            set => _base.ConnectionId = value;
        }

        public override IFeatureCollection Features => _base.Features;

        public override IDictionary<object, object> Items
        {
            get => _base.Items;
            set => _base.Items = value;
        }

        public override IDuplexPipe Transport
        {
            get => _base.Transport;
            set => _base.Transport = value;
        }

        public override void Abort()
        {
            _base.Abort();
        }

        public override void Abort(ConnectionAbortedException abortReason)
        {
            _base.Abort(abortReason);
        }

        public override ValueTask DisposeAsync()
        {
            return _base.DisposeAsync();
        }

        public override CancellationToken ConnectionClosed
        {
            get => _connectionClosed;
            set => _connectionClosed = value;
        }

        public override EndPoint LocalEndPoint
        {
            get => _localEndPoint;
            set => _localEndPoint = value;
        }

        public override EndPoint RemoteEndPoint
        {
            get => _remoteEndPoint;
            set => _remoteEndPoint = value;
        }

        public override bool Equals(object? obj)
        {
            return _base.Equals(obj);
        }

        public override int GetHashCode()
        {
            return _base.GetHashCode();
        }

        public override string ToString()
        {
            return _base.ToString();
        }
    }
}