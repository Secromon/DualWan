using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NetBinder.Shared.Models;

namespace NetBinder.Service.Services;

/// <summary>Network metadata only; never records payload or application headers.</summary>
public sealed class FlowRecord
{
    private readonly object _gate = new();
    private bool _resultWritten;
    private bool _reverseNatLogged;
    private bool _closed;
    private string _outboundSource = "UNKNOWN";
    private string _result = "PENDING";
    public long Id { get; }
    public int Pid { get; }
    public string Process { get; }
    public string Executable { get; }
    public string Rule { get; }
    public string Wan { get; }
    public string Interface { get; }
    public int InterfaceIndex { get; }
    public string InterfaceIpv4 { get; }
    public string Gateway { get; }
    public IPEndPoint Source { get; }
    public IPEndPoint Destination { get; }
    public int RelayPort { get; }
    public DateTimeOffset Started { get; } = DateTimeOffset.Now;
    public bool Override => !Source.Address.ToString().Equals(
        _outboundSource == "UNKNOWN" ? InterfaceIpv4 : _outboundSource, StringComparison.OrdinalIgnoreCase);
    public bool Connected { get; private set; }
    public bool Failed { get; private set; }

    public FlowRecord(long id, int pid, string executable, BindingMapping binding,
        IPEndPoint source, IPEndPoint destination, int relayPort)
    {
        Id = id;
        Pid = pid;
        Executable = executable;
        Process = Path.GetFileName(executable);
        Rule = $"{binding.ProcessName} -> {binding.LogicalWan}";
        Wan = binding.LogicalWan;
        Interface = binding.InterfaceName;
        InterfaceIndex = binding.InterfaceIndex;
        InterfaceIpv4 = binding.InterfaceIpv4;
        Gateway = binding.GatewayIpv4;
        Source = source;
        Destination = destination;
        RelayPort = relayPort;
    }

    public void Created()
    {
        Console.WriteLine($"INFO FLOW CREATED\nFlow ID: {Id}\nProcess: {Process}\nPID: {Pid}\nExecutable: {Executable}\nRule matched: {Rule}\nLogical WAN: {Wan}\nInterface: {Interface}\nInterfaceIndex: {InterfaceIndex}\nInterface IPv4: {InterfaceIpv4}\nGateway: {Gateway}\nOriginal source: {Source}\nDestination: {Destination}\nProtocol: TCP\nAddress family: IPv4\nWAN OVERRIDE: {(Override ? "YES" : "NO")}\nRedirect target: 127.0.0.1:{RelayPort}\nRelay accepted: PENDING\nOutbound connect: PENDING");
    }

    public void RelayAccepted() => Console.WriteLine($"INFO FLOW {Id} Relay: ACCEPTED; destination restored: {Destination}");
    public void InterfaceApplied() => Console.WriteLine($"DEBUG FLOW {Id} IP_UNICAST_IF: {InterfaceIndex} / SUCCESS");
    public void Bound(IPEndPoint local)
    {
        _outboundSource = local.Address.ToString();
        Console.WriteLine($"DEBUG FLOW {Id} Source bind: {local} / SUCCESS");
    }
    public void ConnectAttempted() => Console.WriteLine($"DEBUG FLOW {Id} Outbound connect attempted: {Destination}");

    public void ConnectSucceeded(IPEndPoint local)
    {
        lock (_gate)
        {
            if (_resultWritten) return;
            _outboundSource = local.Address.ToString();
            Connected = true;
            _result = "SUCCESS";
            WriteResult();
        }
        Console.WriteLine($"INFO FLOW ACTIVE\nFlow ID: {Id}\nProcess: {Process}\nPID: {Pid}\nRule: {Rule}\nWAN: {Wan} / {Interface} [{InterfaceIndex}]\nOriginal source: {Source}\nDestination: {Destination}\nOutbound source: {local}\nWAN OVERRIDE: {(Override ? "YES" : "NO")}\nConnect: SUCCESS");
    }

    public void Fail(string operation, Exception? error = null, string? detail = null)
    {
        lock (_gate)
        {
            if (_resultWritten) return;
            Failed = true;
            _result = "FAILED_CLOSED";
            WriteResult();
        }
        string code = error is SocketException socket ? socket.ErrorCode.ToString() : error?.HResult.ToString() ?? "N/A";
        Console.WriteLine($"ERROR FLOW FAILED\nFlow ID: {Id}\nProcess: {Process}\nPID: {Pid}\nRule: {Rule}\nSelected WAN: {Wan} / {Interface} [{InterfaceIndex}]\nOperation: {operation}\nError: {code}\nDescription: {detail ?? error?.Message ?? "Unknown"}\nResult: FAILED CLOSED");
    }

    public void ReverseNatOnce()
    {
        if (_reverseNatLogged) return;
        lock (_gate)
        {
            if (_reverseNatLogged) return;
            _reverseNatLogged = true;
            Console.WriteLine($"DEBUG FLOW {Id} Reverse NAT active: 127.0.0.1:{RelayPort} <-> {Destination}");
        }
    }

    public void Closed(string reason, bool expired)
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            if (!_resultWritten)
            {
                _result = "INCOMPLETE";
                WriteResult();
            }
        }
        Console.WriteLine($"INFO FLOW {(expired ? "EXPIRED" : "CLOSED")}\nFlow ID: {Id}\nProcess: {Process}\nPID: {Pid}\nWAN: {Wan} / {Interface}\nDestination: {Destination}\nDuration: {(DateTimeOffset.Now - Started).TotalSeconds:F3} s\nClose reason: {reason}");
    }

    private void WriteResult()
    {
        _resultWritten = true;
        Console.WriteLine($"ROUTING | {Started:O} | FLOW={Id} | {Process} | PID={Pid} | {Source}->{Destination} | RULE={Rule} | WAN={Wan}/{Interface}[{InterfaceIndex}] | OUT={_outboundSource} | OVERRIDE={(Override ? "YES" : "NO")} | RESULT={_result}");
    }
}
