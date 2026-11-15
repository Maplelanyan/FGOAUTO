# Android Emulator 只读审计

审计日期：2026-11-15，Asia/Shanghai。宿主环境为 i5-11400F（6 核 12 线程）、16 GB RAM、RTX 5060。本轮仅接管已运行的 `FgoAuto_API_34` / `emulator-5554`，没有另启 AVD，没有启动或操作 FGO，没有改模拟器设置、安装包或用户数据。

## 可确认的结论

| 项目 | 本轮证据 | 可支持的判断 |
| --- | --- | --- |
| Android / Emulator | Android 14 / API 34；Emulator 37.2.13 build 16740729；Google Play x86_64 镜像 revision 14 | 当前环境是 x86_64 Android 虚拟机 |
| vCPU / RAM / heap | `config.ini` 为 4 vCPU / 4096 MB / 512 MB heap；guest `/proc/cpuinfo` 列出 processor 0–3；`MemTotal=4016096 kB` | 实际分配与配置基本一致 |
| 显示 | 配置 1280×720 / 187 dpi；`wm size` 为 `Physical size: 1280x720` | 当前真实桌面是 720p |
| CPU 加速 | `emulator.exe -accel-check` 返回 `WHPX(10.0.26200) is installed and usable`；前序启动日志已确认 WHPX operational | 没有证据支持“当前缺少虚拟化加速”；`-accel-check` 本身只是能力检查 |
| GPU 渲染 | 运行命令为 `-gpu host`；SurfaceFlinger 实际报告 NVIDIA RTX 5060 的 OpenGL ES Translator | Android 桌面的 GLES 渲染确实连接到了宿主 NVIDIA GPU |
| FGO 安装 | `pm list packages` 与 `dumpsys package` 均找到 `com.bilibili.fatego`，`installed=true` | B 服 FGO 已装入这台 AVD |
| FGO 运行 | 两次进程查询均没有 FGO；`topResumedActivity` 为 NexusLauncher | 本轮没有游戏运行场景，不能给出 FGO FPS 或游戏内 CPU 结论 |
| ARM 桥 | 系统 native bridge 配置为 `libndk_translation.so`，桥库存在；FGO `primaryCpuAbi=arm64-v8a` | 该 ARM64 包在此 x86_64 环境运行时需要 ARM 转译；不是原生 ARM 执行 |
| H.264 编码 | 实时 codec 清单将 `c2.android.avc.encoder` 标为 software-only=1 / hw-accelerated=0 | 不能因宿主有 RTX 显卡而声称 scrcpy 在此 AVD 使用 NVENC 硬编码 |

## FGO 与 ABI 证据

`dumpsys package com.bilibili.fatego` 的有关字段：

```text
primaryCpuAbi=arm64-v8a
secondaryCpuAbi=null
versionCode=245 minSdk=24 targetSdk=33
versionName=2.129.0
User 0: installed=true hidden=false suspended=false ... enabled=0
```

系统属性：

```text
ro.product.cpu.abi=x86_64
ro.product.cpu.abilist=x86_64,arm64-v8a
ro.product.cpu.abilist32=
ro.product.cpu.abilist64=x86_64,arm64-v8a
ro.dalvik.vm.isa.arm=x86
ro.dalvik.vm.isa.arm64=x86_64
ro.dalvik.vm.native.bridge=libndk_translation.so
ro.enable.native.bridge.exec=1
```

只读扫描 `/system/lib64`、`/system_ext/lib64`、`/vendor/lib64` 找到了 `/system/lib64/libndk_translation.so`、`libndk_translation_exec_region.so` 以及 EGL/GLES/Vulkan 等代理库；未找到名称含 houdini 的库。该扫描仅覆盖这些可读路径，不等同于遍历整台设备。由于 FGO 未运行，没有尝试读取游戏内存映射，也没有测量转译耗时。

前台组件为 `com.google.android.apps.nexuslauncher/.NexusLauncherActivity`。`pidof com.bilibili.fatego` 无结果，`ps -A` 对 fate/fgo/bilibili/lasengle 等关键词也无游戏进程。

## GPU 与编码器证据

SurfaceFlinger 的运行时摘要：

```text
EGL implementation : 1.4 Android META-EGL
GLES: Google (NVIDIA Corporation), Android Emulator OpenGL ES Translator
      (NVIDIA GeForce RTX 5060/PCIe/SSE2), OpenGL ES 3.1 (4.5.0 NVIDIA 621.01)
ro.hardware.egl=emulation
ro.hardware.vulkan=ranchu
```

前序启动日志的 gfxstream、host Vulkan/GLES 成功信息与此一致。GLES renderer 证据验证的是渲染链路，不能延伸为硬件视频编码或 FGO 转译无开销的证明。

此镜像没有名为 `media.codec` 的 dumpsys 服务，该命令返回 `Can't find service: media.codec`。替代的只读 `dumpsys media.player` 给出了实时 MediaCodec 清单，其中 H.264 编码项为：

```text
Encoder "c2.android.avc.encoder" supports
  aliases: [ "OMX.google.h264.encoder" ]
  encoder: 1
  vendor: 0
  software-only: 1
  hw-accelerated: 0
  owner: "codec2::software"
  size-range = "16x16-2048x2048"
  bitrate-range = "1-12000000"
```

清单中的 `c2.goldfish.h264.decoder` 标为硬件加速的是**解码器**，不能用它证明编码器加速。清单中的 `measured-frame-rate-*` 属于 codec 声明能力，不是本机本轮实测帧率。此处未推送或运行任何新的 scrcpy helper；历史 `.build-tools/VideoProbe/*.txt` 选择 `c2.android.avc.encoder` 的记录与实时清单相符，但历史性能数值没有作为本轮测量使用。

## 原始 RGB gRPC 与 scrcpy 的取舍

当前产品请求 RGB888；1280×720 每帧像素净载荷为 `1280×720×3 = 2,764,800` 字节。若交付 30/60 帧每秒，仅像素净载荷分别约为 82.94/165.89 MB/s，尚未计 protobuf、接收复制、颜色转换和 UI 上传。gRPC screenshot 随设备新帧交付，截图也会按请求尺寸保持比例缩放，接收帧率不应直接解释为 FGO 渲染帧率。[官方协议](https://android.googlesource.com/platform/tools/base/+/54fe2d86/emulator/proto/emulator_controller.proto)

源码里的 RGB/RGBA screenshot 路径调用 renderer 的 `getScreenshot`；gfxstream `FrameBuffer::getScreenshot` 向颜色缓冲发出 Screenshot 命令，写入 CPU 可访问的 `pixels` 并等待完成。由此可推断捕获涉及颜色缓冲读取及同步；本轮未做 GPU 时间线或 GPU readback profiling，所以不能把它宣布为本机已证实的主瓶颈。[ScreenshotUtils 源码](https://android.googlesource.com/platform/external/qemu/+/refs/heads/emu-master-dev/android/android-grpc/services/emulator-controller/server/src/android/emulation/control/utils/ScreenshotUtils.cpp#154)、[gfxstream FrameBuffer 源码](https://android.googlesource.com/platform/hardware/google/gfxstream/+/refs/heads/main/host/FrameBuffer.cpp)

scrcpy 在设备端用 MediaCodec 编码显示 Surface，再把压缩包送到主机解码；可减少原始像素的传输量，但本 AVD 的 H.264 软件编码会消耗 guest CPU，仍需主机解码并转换，收益必须测量。scrcpy 的 `max_fps` 是捕获上限，画面变化决定实际出帧，不能保证游戏帧率。[scrcpy 5.0.1 架构](https://github.com/Genymobile/scrcpy/blob/v5.0.1/doc/develop.md#screen-video-encoding)、[帧率说明](https://github.com/Genymobile/scrcpy/blob/v5.0.1/doc/video.md#frame-rate)

旧 `.build-tools/PreEmulatorMigration/Services/AdbVideoPreview.cs` 请求 `max_size=864 max_fps=30 video_bit_rate=4000000`。其输出尺寸、捕获上限和压缩质量均不同于全尺寸 RGB888，不能只比较客户端帧数就断言更快。后续对照应使用相同动画负载、分辨率和统计窗口，并分别记录 guest/qemu CPU、客户端 CPU、实际捕获与源动画帧率、图像匹配结果。

## 有限的空闲基线

在 Android 桌面、无 FGO、无预览流时，对既有 qemu PID 27812 取了单个约 5.034 秒样本：

| 指标 | 结果 |
| --- | ---: |
| qemu CPU，单核满载=100% | 15.83% |
| qemu Working Set | 3810.6 MiB |
| qemu Private Bytes | 5573.4 MiB |
| 进程线程数 | 113 |
| guest MemAvailable（相邻时间点） | 2427468 kB |

这只是带有后台 Play 服务的空闲样本，不是持续性能基准。Working Set 与 Private Bytes 含义不同，不能相加作为实际物理内存使用量；本样本也不能说明给 AVD 增加 RAM/vCPU 会改善游戏性能。

## 清理状态

审计继承的拥有实例为 emulator PID 21648、qemu PID 27812，端口 5554。完成设备查询后执行了定向命令：

```text
adb -s emulator-5554 emu kill
OK: killing emulator, bye bye
OK
```

随后查询确认两个 PID 均已退出，`adb devices` 为空。没有 kill 所有 qemu，没有删除或重置 AVD、游戏或 SDK，没有新增 helper、ADB forward、游戏触控或设置修改。该关闭状态在交回唯一 AVD 供后续性能探针使用时已核实；后续独立测试可能再次启动同一 AVD。

本报告只保存筛选后的诊断字段，没有保存 gRPC bearer token、完整 discovery 文件或未筛选启动日志。
