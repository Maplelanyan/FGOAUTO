using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

sealed class AnimationServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private string _phase = "load";
    public string Phase { get => Volatile.Read(ref _phase); set => Volatile.Write(ref _phase, value); }
    public ConcurrentQueue<JsonElement> Reports { get; } = new();
    public int ReportCount => Reports.Count;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public AnimationServer() { _listener.Start(); _ = Run(); }
    private async Task Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { var client = await _listener.AcceptTcpClientAsync(_stop.Token); _ = Respond(client); }
            catch (OperationCanceledException) { break; }
        }
    }
    private async Task Respond(TcpClient client)
    {
        using (client)
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
            var request = await reader.ReadLineAsync(); if (request is null) return;
            var length = 0; string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
            var path = request.Split(' ')[1];
            var response = Page;
            if (path.StartsWith("/phase")) response = Phase;
            else if (path.StartsWith("/report"))
            {
                if (length is > 0 and < 500_000)
                {
                    var body = new char[length]; var offset = 0;
                    while (offset < body.Length) { var count = await reader.ReadAsync(body.AsMemory(offset)); if (count == 0) break; offset += count; }
                    using var document = JsonDocument.Parse(new string(body, 0, offset)); Reports.Enqueue(document.RootElement.Clone());
                }
                response = "ok";
            }
            var bytes = Encoding.UTF8.GetBytes(response);
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {(path == "/" ? "text/html" : "text/plain")}; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header); await stream.WriteAsync(bytes);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { }
    }
    public void Dispose() { _stop.Cancel(); _listener.Stop(); _stop.Dispose(); }
    private const string Page = """
<!doctype html><html><head><title>FgoAuto diagnostic animation</title><meta name="viewport" content="width=device-width, initial-scale=1">
<style>body{margin:0;overflow:hidden;background:#091629;color:white;font:24px sans-serif}#box{position:absolute;left:0;top:160px;width:180px;height:180px;background:linear-gradient(45deg,#00adff,#ac00ff);will-change:transform;border-radius:25px}#label{padding:22px}</style></head>
<body><div id="label">FgoAuto diagnostic animation</div><div id="box"></div><script>
let phase='load',last=0,samples=[],currentPhase=phase,stopped=false;
let polling=setInterval(async()=>{try{phase=await(await fetch('/phase')).text();if(phase==='done'){stopped=true;clearInterval(polling);clearInterval(reporting);document.querySelector('#label').textContent='FgoAuto diagnostic finished';}}catch{}},150);
function tick(t){if(stopped)return;if(last&&phase===currentPhase)samples.push(t-last);else{samples=[];currentPhase=phase;}last=t;
let x=(Math.sin(t/600)+1)*(innerWidth-200)/2;let y=Math.sin(t/430)*75;
document.querySelector('#box').style.transform=`translate3d(${x}px,${y}px,0) rotate(${t/30}deg)`;
requestAnimationFrame(tick)}requestAnimationFrame(tick);
let reporting=setInterval(()=>{let data=samples;samples=[];if(stopped||!data.length)return;let sorted=[...data].sort((a,b)=>a-b),sum=data.reduce((a,b)=>a+b,0);
document.querySelector('#label').textContent=`${phase}: ${(1000*data.length/sum).toFixed(1)} source rAF FPS`;
fetch('/report',{method:'POST',body:JSON.stringify({phase:currentPhase,frames:data.length,milliseconds:sum,fps:1000*data.length/sum,p95:sorted[Math.ceil((sorted.length-1)*.95)],max:sorted.at(-1),visible:document.visibilityState})}).catch(()=>{});},1000);
</script></body></html>
""";
}
