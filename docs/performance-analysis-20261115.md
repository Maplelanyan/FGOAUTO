# FgoAuto 性能分析与优化记录

日期：2026-11-15。基线提交 `a5d4937`；.NET 10、Avalonia 12.1.4、OpenCV 4.13、Emulator 37.2.13 / Android 14 Google Play x86_64 r14。宿主 i5-11400F（6C/12T）、16GB RAM、RTX 5060。保留现有 AVD、游戏、账号、规则与模板，未修改虚拟化设置、vCPU、RAM 或系统镜像。

## 1. 结论与证据边界

现有证据支持两个明确的宿主机成本：持续 RGB 截图传输，以及多模板识别的 CPU 负载。Avalonia 收到完整帧后的处理通常只占几毫秒。ARM64 FGO 在 x86_64 客体上需要 Native Bridge，这是额外架构成本，但本轮没有测出它在战斗中的耗时比例，不能称为已量化的唯一根因。

三种帧率分别记录：Chrome rAF 是测试动画回调率；gRPC FPS 是客户端收到的完整截图率；DrawImage FPS 是窗口提交的新帧数，均不能冒充 FGO 或显示器实际 FPS。下列模式短采样按顺序执行，存在启动负载与时间变化；CPU 数据用于定位成本，不能把相邻相位的 FPS 差全算成某个开关的因果收益。

## 2. 模拟器与游戏兼容性

详见 [设备审计与筛选后的命令证据](emulator-audit.md)。本轮确认：

- `-accel-check` 检测到 WHPX installed and usable，实际运行日志另有 Windows Hypervisor Platform accelerator operational。
- `-gpu host` 的运行日志和 SurfaceFlinger 都指向 NVIDIA RTX 5060 的 OpenGL ES Translator / gfxstream，未发现当前回退 SwiftShader 的证据。宿主显卡渲染成立，不等于硬件视频编码成立。
- 4 vCPU、4096MB、512MB heap 配置未变，客体实际列出四个处理器；显示实际为 1280×720。Android 空闲时 MemAvailable 约 2.4GB，不支持无依据地增加内存。512MB 是 Java heap 配置，不能据此推断 Unity 原生游戏的全部内存上限。
- 已装 B 服 `com.bilibili.fatego` 2.129.0，`primaryCpuAbi=arm64-v8a`；系统 `abilist=x86_64,arm64-v8a`，32位 ABI 空，Native Bridge 为 `libndk_translation.so`，相关桥库存在，未在审计路径找到 Houdini。
- 后续工具确实启动到 FGO 的 `UnityPlayerNativeActivity`，进程存活并显示启动画面；没有点击、登录、接受条款、确认下载或进入战斗。SurfaceFlinger 的三个 FGO SurfaceView 返回零帧时间，仅有 16,666,666ns 刷新周期。该周期是显示配置，不能据此宣称 FGO 60 FPS。
- 已有标准/OpenGL 选项继续保留。前轮实际窗口对照约 53.0 / 51.6 提交FPS，没有证据把关闭 Vulkan 作为通用提速方案。本轮不强制改后端，也不将零星 ColorBuffer 报错当成所有低帧的原因。

## 3. 静态问题排序

| 优先级 | 问题与证据 | 影响 | 本轮处理 / 风险 |
|---|---|---|---|
| 高 | 32条实际规则只读匹配时，FgoAuto CPU约235–270%（单逻辑核=100%）；每轮约81–132ms，近5s几乎持续匹配 | 与模拟器争用CPU，20Hz只是上限，实际达不到 | 复用同帧搜索缩放、去除矩阵复制、缓存查询，保持搜索区域和判定语义；低风险 |
| 高 | RGB888 720p每帧2.7648MB，60fps净像素约165.9MB/s；实际进程分配常为120–270MB/s | 读回/传输/分配及GC成本，持续截图即使无人查看也有开销 | 增加真实停流与停转换对照；保留完整帧所有权，未冒险复用或换MMAP |
| 中 | 普通匹配复制分数矩阵，重复目标逐查询重复缩放相同搜索区域和复制模板 | 多余原生内存流量和CPU | 改为原地抑制分数、每帧共享相同ROI缩放、只读缓存模板；奇数尺寸保持原Resize边界 |
| 中 | 模板缓存的Mat没有确定性释放 | 长会话累计持有原生资源 | IDisposable与读写锁，等待进行中的匹配完成后释放；未在并发读者使用时归还内存 |
| 中 | 缺少停流时资源日志、QEMU CPU/RAM、每轮识别及自动触控RPC统计 | 无法分辨游戏、传输和识别开销 | 后台按拥有的QEMU PID采样，补每轮识别、全部触控和分配/GC日志 |
| 中 | 若仅在UI丢帧，转换线程仍可能处理不能及时显示的帧 | UI受阻时浪费转换工作 | 待显示槽未消费时暂不转换；UI消费后仅在原始帧更新时通知，保留最终最新完整帧 |
| 可靠性 | 诊断暂停/恢复会引入旧任务、旧坐标和静态帧恢复问题 | 可能误点、取消新流或遗留进程 | session隔离、停止时观察全部任务、epoch校验、恢复静态画面只确认一次新截图后允许触控 |

没有把已存在的最新完整帧覆盖、后台转换、池化BGRA、单UI回调、原始像素匹配、灰度/模板缓存、两查询线程及触控移动合并重写一遍。

## 4. 具体修改

- `AndroidEmulatorService.cs`：会话级诊断开关；停/恢复截图流而不停止游戏；独立session CTS与观察器；故障停止清理；按QEMU PID采样所需标识；全部触控RPC时序；预览背压与30fps工作调度。正常模式不等待短定时器。
- `PerformanceDiagnostics.cs`：五种互斥会话模式、识别epoch及抵消定时器迟醒的预览节流。诊断不写AVD或规则文件。
- `MainViewModel.Diagnostics.cs`、`MainViewModel.Preview.cs`、`Views/MainWindow.axaml`：折叠式诊断入口，保留UI风格；停流时继续记录资源；旧图不可交互，上传恢复后的帧才允许手动触控。
- `ProcessResourceMonitor.cs`、`RecognitionStatistics.cs`、`RpcStatistics.cs`、`PreviewPipelineStatistics.cs`、`FrameStatistics.cs`：后台CPU/内存采样、近5s阶段/匹配/RPC统计、转换计数；一次性确认帧不破坏流序号统计。
- `EmulatorGrpcClient.cs`：contextual deserializer使用独立的未预清零数组，随后完整覆盖；移除格式头的小数组复制。仍保留gRPC返回后可安全异步持有的存储，不减少每帧像素分配字节。
- `ImageMatchService.cs`、`MainViewModel.cs`：移除多余矩阵复制、共享ROI缩放、复用查询、释放模板缓存；保留原阈值、歧义检查、全屏/卡片区域、点击前复核和触发次数。诊断变化使在途匹配结果失效，暂停期间不执行点击。
- `tools/PerformanceProbe`：真实窗口/同AVD的可复现对照、可选只读匹配负载与video-only H.264实验，产品不新增外部设备连接入口。
- `tests/FgoAuto.PerformanceChecks`：增加传输所有权、匹配语义、并发释放、诊断生命周期、定时器抖动以及选卡/宝具/次数上限兼容检查。

## 5. 可复现测量

本地原始结果位于 `.build-tools/performance-20261115/`（不提交用户数据/画面）。`optimized.json` 为8秒相位完整对照，`final.json` 为5秒复测，均为同一真实窗口与32条已保存规则的只读负载，自动执行为false、触控次数为0。初始一次工具的FGO层名转义错误记录在 `diagnostics.json`，保留供审计，不作为完整通过结果；后两轮修复后failure均为null。

### 当前实际窗口的成本分布

5秒复测后半段（已过启动瞬态），CPU均以一个逻辑核心100%计：

| 相位 | 源rAF | 完整截图/提交 FPS | 客户端CPU | QEMU CPU | 客户端分配 |
|---|---:|---:|---:|---:|---:|
| 暂停截图流，同时暂停只读识别 | 53.3 | 0 / 0 | 3.7% | 312.9% | 0.16MB/s |
| 暂停识别，保留正常RGB预览 | 51.3 | 52.3 / 52.3 | 75.2% | 268.6% | 228.6MB/s |
| 正常模式重复，32条只读识别 | 51.0 | 51.1 / 50.7 | 266.2% | 270.3% | 238.1MB/s |

正常重复相位收帧至提交P95为2.94ms，位图复制P95约0.95ms；5秒GC暂停75.2ms。显示链路成本存在，但不能解释全部低帧。正常最初相位也曾只有约30提交FPS，测试动画自身约25.7 rAF；这进一步说明启动负载和源端不稳定，不把某一短窗口挑成稳定帧率保证。QEMU在停流相位也有后台负载变化，表中不能直接相减估算GPU读回成本。

### 识别前后微基准

对基线与新版本使用同一32查询合成画面、相同两线程设置；按“旧、新、新、旧”顺序重复，每种分辨率每段20轮：

| 分辨率 | 旧版平均耗时（两次） | 新版平均耗时（两次） | 结论 |
|---|---|---|---|
| 720p | 50.70 / 51.21ms | 47.62 / 47.47ms | 平均约减少6.7%；CPU/轮约99.2→94.9ms |
| 1080p | 95.32 / 111.50ms | 104.17 / 100.90ms | 波动覆盖收益，不能宣称明确提升 |

720p这个合成负载中，20轮/秒相当于约1.9个逻辑核心的CPU时间；实际32条模板比合成负载更慢，运行日志必须继续关注。没有为追求此数字缩小搜索范围或降低匹配阈值。

正式复现入口 `--compare-recognition` 也已执行通过（`recognition-repro.log`）：720p旧版50.38/50.82ms，新版45.77/48.83ms，平均约减少6.5%；单段测量和1080p仍有明显波动，不能推广为游戏FPS提升。

传输复制微基准，720p平均0.454→0.410ms，但P95 0.876→1.012ms；1080p平均0.963→0.751ms，P95 1.538→1.484ms。分配字节不变，720p尾延迟没有改善，不能把这个小优化称为解决GC或游戏帧率。实现依据为 [gRPC 2.85的默认反序列化](https://raw.githubusercontent.com/grpc/grpc-dotnet/v2.85.0/src/Shared/DefaultDeserializationContext.cs) 和 [.NET 10 ToArray](https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/libraries/System.Memory/src/System/Buffers/BuffersExtensions.cs)。

### RGB与H.264

H.264在同一AVD、1280×720、max60fps、12Mbps上限、软件编码/FFmpeg软件解码下确实跑通，两次有效短样本解码约36.5–43.7fps。`final.json` 中编码流约0.34MB/s、客户端分配0.28MB/s、CPU20.3%、QEMU352.9%；选择的编码器是 `c2.android.avc.encoder`。与暂停识别的RGB相位相比，主机开销明显下降，但QEMU开销更高、解码帧率更低。它还没有包含UI上传、PTS到呈现延迟、压缩后模板精度及自动点击安全验证。

因此没有直接替换。低复杂度测试动画的VBR码率不能推广为战斗码率；本机AVD的软件编码也不能称为NVENC。[scrcpy编码架构](https://github.com/Genymobile/scrcpy/blob/v5.0.1/doc/develop.md#screen-video-encoding)

### 30 FPS节流

限速在转换前执行，截图流及识别频率不变。测试发现逐帧叠加 `Task.Delay` 的Windows迟醒会把限速进一步压低，并增加延迟；最终改为累计截止时间、最多一帧追赶，并补确定性抖动测试。普通模式完全不做该等待。实际限制后的FPS受源画面和主机负载影响，不承诺恰好30。

最终独立重测记录在 `preview30.json`：8.01秒接收468帧、转换240帧，接收58.42 FPS、提交29.96 FPS，源动画59.2 rAF。收帧至提交P95为26.70ms，体现主动限速的显示延迟取舍；原始截图仍全速到达，不能声称因此减少GPU读回、gRPC带宽或整体分配。此项不默认启用。

## 6. 验证与限制

Release编译与统一回归覆盖：RGB方向/颜色、720p模板映射、列表和重复卡片、场景复核、源序号和时间窗口、分段gRPC独立存储、匹配唯一性和阈值、奇数ROI、并发Dispose、旧observer隔离、异常任务清理、暂停不进行额外截图、0–3张宝具优先/三张独立卡/重复手牌保护/触发上限、慢触控RPC合并与释放。

没有执行自动游戏点击或消耗游戏资源。FGO仅验证安装/ABI/进程与启动画面，未测战斗FPS、持续周回、ARM桥热点、主机GPU时间线或实际显示器呈现；SurfaceFlinger未给出有效游戏帧时间。触控本轮主要依靠确定性模拟RPC回归，真实点击延迟需在固定游戏场景继续测量。短动画测试不能替代长时间稳定性、热降频、后台服务或游戏版本差异验证。

## 7. 剩余架构限制与下一步

1. 先在同一固定FGO战斗/动画场景运行这些诊断，分别测“识别开/关、预览开/关、截图流开/关”，配合Android/主机CPU采样及帧时间追踪，量化ARM转译与读回的份额。当前不能保证稳定60FPS或达到成熟游戏模拟器。
2. 通过分配栈定位RGB每帧独立数组之外的分配。要真正消除LOH负担，需要带引用计数/租约的帧所有权设计，并验证所有识别与UI读者结束后才能复用；不能直接池化当前公开持有的GameFrame。
3. H.264可作为预览专用候选，继续测PTS延迟、图像精度和软件编码对游戏的影响；若同时保留RGB识别，必须测两条捕获路径叠加成本。不会因为带宽低就默认改用有损画面识别。
4. 若优化后游戏仍受Native Bridge/图形兼容限制，需要对引擎与镜像做独立兼容性项目。当前镜像可启动FGO，不等于已经是游戏性能最优镜像；本轮没有迁移、删除或替换它。

复现命令见 [工具说明](../tools/PerformanceProbe/README.md)。正式客户端功能保持完整，诊断模式默认正常；原始帧、规则组、模板逻辑坐标、点击复核和触发次数约束继续生效。
