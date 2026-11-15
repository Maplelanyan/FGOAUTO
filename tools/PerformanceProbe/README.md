# 实际窗口性能对照工具

工具打开产品的真实 `MainWindow/MainViewModel`，启动同一个受管理 AVD，在 Chrome 中播放仅本机可访问的持续动画。先确认 Android 完成启动且 Chrome 保持前台，再按以下顺序测量，每段默认 10 秒，切换后先稳定 2 秒：正常、暂停预览、30 FPS 预览、暂停截图流、暂停识别、正常重复。模式切换必须保持同一 QEMU PID；暂停流期间不得收到截图；恢复后检查新完整帧。源动画不足或进入后台会将测量标为失败。

先关闭现有 FgoAuto/Android Emulator；工具拒绝接管别的运行实例。默认不运行自动规则、不发送触控、不安装应用、不处理许可或登录。结束后停止本地测试页、精确关闭本次 URL 的标签、恢复原 Android 前台和图形偏好，并正常停止拥有的模拟器。最后运行模式元数据会保留，供下一次启动判断是否需要冷启动。不要在测量期间操作游戏或运行其他基准。

```powershell
dotnet run --project tools/PerformanceProbe -c Release -- --seconds 8 --recognition-load --h264 --output E:\FGOAUTO\.build-tools\perf\run.json
```

- `--recognition-load`：只读载入当前规则的模板，调用相同匹配器，最多 20 Hz；不执行命中动作。暂停识别/流时停止该负载。省略时，暂停识别相位只验证开关，不证明识别性能收益。
- `--h264`：额外停止 gRPC，运行 video-only scrcpy 5.0.1，1280px、max60fps、12Mbps，FFmpeg 软件解码并转换 BGRA。此阶段不上传 Avalonia UI、不执行识别，记录实际解码帧率、编码字节、两端 CPU/分配和编码器名称。它会推送并删除本次随机命名的 `/data/local/tmp/fgoauto-perf-*.jar`，清理自己的 ADB 转发；没有向产品恢复外部设备连接流程。码率是上限，实际值随画面复杂度变化；没有端到端 PTS 延迟和压缩后匹配准确率结论。
- `--observe-fgo`：对照结束后仅启动已安装的 B 服 FGO，观察 12 秒，记录进程和 SurfaceFlinger 可读时间戳并保存画面。不会点击、登录、接受条款或确认下载。启动画面及设备刷新周期不能当作战斗 FPS；全零时间戳表示该接口没有提供有效帧时间。
- `--standard` / `--opengl`：临时选择此次启动图形模式；其他变量保持一致。现有偏好在退出时恢复。
- `--seconds 5..30`：每个正式统计窗口长度。
- `--only-preview30`：仅重测预览限速，用于核对定时器抖动修复；省略时执行全部六个相位。
- `--describe-only`：只打印工具和 GC 配置，不创建窗口或启动模拟器。

JSON 包含浏览器源 rAF 回调率、截图接收率、`DrawImage` 提交率、近 5 秒阶段 P95、CPU、内存、分配和 GC。三个帧率含义不同；不会通过重复绘制静止帧提高计数。CPU 单位为单个逻辑核心 100%；工作集与专用内存不能相加。UI 提交不等于显示器呈现；本工具不测 GPU 时间线。结果及 PNG 请放入被 Git 忽略的 `.build-tools`，不要提交账号画面或原始用户运行记录。

## 识别算法前后对照

```powershell
dotnet run --project tools/PerformanceProbe -c Release -- --compare-recognition
```

脚本读取基线提交 `a5d4937` 的匹配器，配合同一固定合成fixture，按旧/新/新/旧顺序分别测20轮，生成文件仅放在 `.build-tools/recognition-ab`，不切换或修改工作区。基线额外的空Dispose只是为了编译共同测试fixture，不改变旧算法。请关闭模拟器及其他基准后单独运行，避免CPU争用污染结果；该结果不能当作FGO FPS。
