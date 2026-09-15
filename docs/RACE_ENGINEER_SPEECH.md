# 语音工程师输出

## 当前装配

工程师默认关闭，默认来源为无需联网的 Windows SAPI。地产赛事页的「语音服务…」打开独立浮窗，可选择 Windows 本地音色、ElevenLabs、Azure Speech、腾讯云、阿里云智能语音交互、千问 AI 平台或 MiniMax；主页面保留当前服务摘要、音量、试听和立即静音。原有 AppSettings 开关、静音与音量无需迁移。

## ElevenLabs

在「语音服务…」选择 ElevenLabs，填写自己的 API Key 和 Voice ID，也可点击「获取音色」读取账户音色列表。模型可选 Flash v2.5（默认）、Multilingual v2 和 Eleven v3；播报语言可跟随界面或固定中文／英文。保存后使用工程师旁的「试听」，它和比赛播报走同一合成、缓存、音量与无线电提示音流程。取消浮窗不应用草稿；切换服务会取消旧输出和待播队列。

仅在用户选择并保存 ElevenLabs 后，播报／试听才发送文本到官方 `api.elevenlabs.io`。不上传原始遥测、录音或自定义提示音。合成会消耗 ElevenLabs 账户额度；读取音色也需要相应 API 权限。密钥通过 `xi-api-key` 请求头发送，不放在 URL、播报文本或错误详情中，不内置共享密钥。HTTP 重定向被禁用。

适配器调用 `POST /v1/text-to-speech/{voice_id}?output_format=pcm_24000`。`GET /v2/voices` 使用 `has_more` 和 `next_page_token` 分页，每页最多读取 1 MiB，最多 20 页；浮窗关闭会取消读取。

实现参考：[语音合成](https://elevenlabs.io/docs/api-reference/text-to-speech/convert)、[账户音色](https://elevenlabs.io/docs/api-reference/voices/search)、[API 认证](https://elevenlabs.io/docs/api-reference/authentication)、[模型](https://elevenlabs.io/docs/overview/models)。

## Azure Speech

在同一「语音服务…」浮窗选择 Azure Speech，填写 Speech 资源密钥、资源区域代码（例如 `eastasia`，须与密钥所属资源一致）和音色 ID。点击「获取音色」从所填区域读取可用的中英文音色，选择后自动填写 `ShortName`，例如 `zh-CN-XiaoxiaoNeural` 或 `en-US-JennyNeural`；也可手动填写。语言默认跟随音色，也可固定中文／英文；固定语言应选择支持该语言的音色。保存后用主页面「试听」验证，提示音、音量、静音和回退设置与 ElevenLabs 共用。

当前支持 Azure 全球公有云的区域端点及预构建音色，不支持中国云、美国政府云、自定义域名／私有端点、Entra 认证或需要部署 ID 的自定义声音。区域栏只接受区域代码，不能填写 URL。获取音色只读取目录，不合成音频；仅保存并选用 Azure 后，播报／试听才向该区域发送文本并消耗 Azure 账户额度，不上传原始遥测、录音或提示音。

适配器通过 `Ocp-Apim-Subscription-Key` 请求头认证，调用 `https://{region}.tts.speech.microsoft.com/cognitiveservices/v1`，将纯文本安全转义为 SSML，输出 `raw-24khz-16bit-mono-pcm`。音色读取路径为 `/cognitiveservices/voices/list`，响应上限 4 MiB。音色 ID 与区域在发出请求前校验，密钥不会放在 URL 或 SSML 中。

实现参考：[Azure Text to speech REST API](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/rest-text-to-speech)、[SSML 文档结构](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/speech-synthesis-markup-structure)、[主权云与端点区别](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/sovereign-clouds)。

## 腾讯云

填写开通语音合成权限的 SecretId、SecretKey 和数字音色 ID。默认音色为智瑜 `101001`，浮窗提供智瑜、智云、智瑞和英文 WeJack 预设，也可通过官方音色列表查找其他音色。当前接入基础语音合成 `TextToVoice`（API `2019-08-23`），向 `https://tts.tencentcloudapi.com/` 发送 TC3-HMAC-SHA256 签名请求，使用 16 kHz 单声道 PCM。SecretKey 不发送给服务端；签名覆盖实际 UTF-8 请求体。响应必须匹配本次 SessionId。

这是短句播报接口：中文／混合文本保守限制为 150 字符，纯 ASCII 文本为 500 字符；超长内容报配置错误，不截断。当前不配置 STS 临时凭据或需要 FastVoiceType 的一句话复刻音色，不创建或训练声音。

参考：[基础语音合成](https://cloud.tencent.com/document/api/1073/37995)、[TC3 签名](https://cloud.tencent.com/document/api/213/30654)、[音色列表](https://cloud.tencent.com/document/product/1073/92668)。

## 阿里云智能语音交互（NLS）

这里接入的是上海地域的**智能语音交互**，使用项目 AppKey，以及拥有相关权限的 RAM AccessKey ID / AccessKey Secret。三项凭据分别加密保存。默认音色 `xiaoyun`，浮窗提供少量常用音色和官方目录入口。它与千问模型服务是不同产品，不能交换凭据或音色 ID。

适配器通过 HTTPS POST 调用 `nls-meta.cn-shanghai.aliyuncs.com` 的 `CreateToken`，采用 POP HMAC-SHA1 签名；Token 只在内存缓存，按返回的 ExpireTime 在到期前一分钟更新，并发请求共用一次刷新。随后使用 `X-NLS-Token` 请求 `nls-gateway-cn-shanghai.aliyuncs.com/stream/v1/tts`，输出 16 kHz PCM。令牌获取与合成共用一次 6 秒期限。Token 被拒绝时只标记缓存失效，不重试已经提交的合成。

单句最多 300 字符，超过限制明确失败，不接受服务端静默截断。当前不配置其他地域、STS 或用户手填的短期 Token。

参考：[NLS RESTful API](https://help.aliyun.com/zh/isi/developer-reference/restful-api-3)、[获取 Token](https://help.aliyun.com/zh/isi/getting-started/use-http-or-https-to-obtain-an-access-token)、[音色及错误码](https://help.aliyun.com/zh/isi/developer-reference/overview-of-speech-synthesis)。

## 千问 AI 平台

使用千问 AI 平台北京地域的按量 API Key，当前支持 `qwen3-tts-flash`（默认）和 `qwen3-tts-instruct-flash`。默认音色 `Cherry`；可从常用音色选择，也可填写该模型支持的音色名称。此入口不使用 NLS AppKey、AccessKey 或 NLS Token，也不接受 `sk-sp-` 开头的 Token Plan 密钥。北京端点与相应 API Key 配套使用。

按照千问平台文档，调用 `https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation`，以 Bearer 鉴权，并通过 `X-DashScope-SSE: enable` 接收 Base64 PCM 片段。完整结束后才交给现有无线电播放流程，采样率为 24 kHz；不会跟随响应中的音频 URL。缺少完成标志、数据中断或中途错误会丢弃整段音频，避免播放半句。

当前不接入 CosyVoice、Qwen-Audio-TTS、Realtime、声音创建或指令控制参数；这些模型系列的调用形状不同，不能只替换模型名称。

参考：[千问平台语音指南](https://platform.qianwenai.com/docs/developer-guides/speech/tts)、[Qwen-TTS API](https://platform.qianwenai.com/docs/api-reference/speech-synthesis/qwen-tts)、[Qwen-TTS 音色](https://platform.qianwenai.com/docs/developer-guides/speech/voice-list/qwen-tts)。

## MiniMax

选择密钥对应的中国站或国际站，填写 MiniMax API Key；通过「获取音色」读取系统、复刻及已生成的账户音色，也可填写 ID。带空格和括号的官方音色 ID 可正常使用。默认 `speech-2.8-turbo`，也提供 2.8 HD、2.6 Turbo / HD、02 Turbo / HD；模型和账户站点分别保存。此配置连接所选站点的 MiniMax 官方 API。

中国站为 `https://api.minimax.cn`，国际站为 `https://api.minimax.io`。`POST /v1/t2a_v2` 使用 Bearer 鉴权，非流式请求返回 hex 编码的 24 kHz 单声道 PCM；校验业务状态码、完整状态和音频元数据后解码。`POST /v1/get_voice` 只读取音色目录，不合成；关闭浮窗会取消读取。不调用声音创建或训练接口，复刻音色的授权及首次使用费用以平台规则为准。

参考：[中国站同步合成](https://platform.minimax.cn/docs/api-reference/speech-t2a-http)、[国际站同步合成](https://platform.minimax.io/docs/api-reference/speech-t2a-http)、[音色查询](https://platform.minimax.cn/docs/api-reference/voice-management-get)、[业务错误码](https://platform.minimax.cn/docs/api-reference/errorcode)。

## 在线服务共同行为与兼容

凭据使用 Windows DPAPI 当前用户保护后，与服务、模型、语言及音色选择一起写入单个 `raceEngineer.speechService.v1` AppSettings 值。六家在线服务分别保存配置，切换服务不会覆盖另一家的配置。备份只包含凭据密文；换电脑／Windows 用户后通常需要重新填写。浮窗保持草稿，只有保存才更换输出；取消或关闭不应用修改。

保留旧 ElevenLabs / Azure 字段，新服务使用各自可选的嵌套设置。缺少 `ProviderId` 时沿用旧 `UseElevenLabs`，未知来源回到 Windows。选用其他服务时旧布尔值为 false，因此旧应用会安全回到 Windows，不会将别家的凭据发送到 ElevenLabs；旧应用重新保存可能丢弃不认识的新字段。更早的应用忽略整个服务设置；新应用仍兼容原 `raceEngineer.voiceId`。没有数据库结构或地产协议变更。

可启用「服务不可用时使用 Windows 本地语音」（默认勾选）。在线合成限时 6 秒；认证、额度限制、网络及无效响应失败后可用同语言的 Windows 默认音色播报，并在主页面提示。普通失败暂停在线尝试至少 60 秒，认证／配置失败至少 5 分钟；遇到 `Retry-After` 延长等待，最多 1 小时。不会自动重试同一次付费 POST；冷却后由下一次播报尝试在线服务。回退声音不进入在线声音缓存。关闭回退时，故障只停用语音输出；重新保存服务设置或关闭再启用工程师可重试。主动静音、赛事切换和退出只取消请求，不触发回退。

适配器共用有界 HTTP 传输，禁止重定向，错误仅暴露分类和重试时间，不显示服务端原文。不上传原始遥测、录音或自定义提示音。最终音频为 16 或 24 kHz 单声道 PCM16，最多 30 秒；同时限制 HTTP 响应和解码后的大小，即使响应不带 `Content-Length` 也会检查。HTTP 200 中的业务错误和 JSON 不能作为 PCM 播放。模型、区域和声音随输出实例固定；原有 32 条／4 MiB 内存缓存继续生效，不持久保存合成音频。

`AzureSpeechTests`、`ElevenLabsSpeechTests`、`CloudSpeechProviderTests` 覆盖官方请求格式、签名、Token 缓存和续期、认证与错误脱敏、无付费重试、音色目录、区域边界、流式响应及解码上限、取消／释放、回退冷却和设置兼容。`EngineerSpeechSettingsWindowTests` 检查六家服务的中英文浮窗、窄宽度布局、草稿隔离、持久化和不可解密凭据的保留。测试使用 HTTP 替身，不消耗账户额度；真实区域、密钥权限、音色可用性和听感仍需使用用户自己的资源试听确认。

## 本地音色与原有设置

音色、音量、接通音／断开音的独立开关和自定义音频都自动保存到当前数据目录，重启后继续沿用已保存的设置。恢复默认提示音只在用户主动点击对应按钮时执行；关闭提示音开关不会删除已导入的音频。

「Windows 音色」异步枚举本机 SAPI 可用的中英文音色，并以声音 ID 保存到语音服务设置（兼容旧 `raceEngineer.voiceId`）。默认项跟随界面语言；显式选择英文音色时，赛事播报和试听均使用英文。切换先取消并释放旧输出及待播队列，再建立新输出，避免音色缓存或播放重叠。保存的音色不存在时提示并回退默认；枚举失败不影响比赛。系统设置中可见的语音不一定全部向 SAPI 开放，列表以实际枚举为准。

`RaceEngineerObserver` 只读取已有旗语、处罚、个人最快圈和进站预测。预测措辞继续保留不确定性。`RaceEngineer` 负责优先级、事件去重、分类冷却、过期、16 条队列及赛事阶段隔离，不识别服务商。输出故障会停用本次启用周期的播报；比赛逻辑继续运行，关闭再启用工程师可重试。

## 接口与所有权

地产赛事页的「试听」使用同一输出、音量与提示音，从六条中英文赛事示例中随机选择一句，避免连续相同。无需连接房间或开启自动播报，但遵守静音／零音量。试听一次最多 30 秒；再次点击停止，真实赛事消息、赛事切换、静音或退出均可取消。试听不写入赛事事件去重、分类冷却或观察器状态，也不清除真实比赛的待播消息。

| 契约 | 责任 |
| --- | --- |
| `ISpeechOutput` | 比赛队列调用的可取消播报入口；队列退出时异步释放输出 |
| `ISpeechSynthesisProvider` | 用纯文本、语言、可选声音 ID 和语速生成 `SpeechAudio`；提供稳定 `Id` 和 `Local`／`Online` 位置标识 |
| `ISpeechAudioPlayer` | 播放 PCM 并应用音量；取消完成前停止自己持有的设备 |
| `RadioSpeechOutput` | 管理合成超时、内存缓存、串行播放及原创接通／断开音；拥有并释放注入的合成器与播放器 |

这些契约位于无 WPF／网络 SDK 依赖的 `LazyForza.Speech` 项目。合成器与播放器由可信代码显式装配，不加载任意插件 DLL。一个输出实例绑定一个合成器、语言、声音 ID 和语速；替换这些设置应释放旧输出并建立新实例，以免复用旧声音缓存。

合成器实现须遵守以下约定：

- 请求是纯文本；如服务使用 SSML，适配器负责正确转义。`Rate=1` 表示默认语速，公共输出允许 0.5–2。声音 ID 的实际映射由适配器处理。
- 返回自有的交错 PCM16 小端数据，采样率 8–48 kHz，单／双声道，长度大于零且不超过 30 秒。`SpeechAudio` 检查对齐和长度并复制输入；压缩音频须由适配器先解码，并在读取／解码过程中限制响应大小，避免先无界分配再验证。
- 合成器不播放提示音、不控制比赛队列，不把音量写入合成缓存。播放器统一按 0–100 音量缩放，静音不发起合成请求。
- 取消必须及时终止自身请求；新请求可能在旧请求取消期间进入，因此须允许最多两次重叠。释放合成器时取消并清理仍未结束的任务、连接、流或推理资源。
- 服务凭据属于具体适配器的私有配置，不属于 `SpeechSynthesisRequest`，也不应出现在播报文字或错误状态中。`Location` 仅描述位置，不提供联网授权或自动回退；在线服务接入需另行明确配置与启用行为。

Windows 实现使用 SAPI 内存流合成 24 kHz 单声道 PCM；每个请求的 COM 对象在其 STA 线程释放。播放器持有并复用自己的 waveOut 句柄，相同格式的起止音与语音无需重复打开设备；格式变化时重新打开，释放播放器时关闭。取消通过 reset／unprepare 停止音频并归还缓冲区，不使用全局停止音效的 API；驱动无完成通知时在预计时长后额外等待至多 2 秒，再结束并清理。

## 取消、超时与缓存

合成默认超时 10 秒（构造时可设为大于零且不超过 30 秒），文本上限 512 字符。完整 PCM 准备好后才依次播放接通音、停顿 180 ms、语音、停顿 220 ms、断开音，避免服务或模型启动时先播接通音后长时间无声。`RadioTransmission` 为整次播报固定一份提示音与停顿快照，修改设置不会混用两组起止音。停顿使用可取消延时，任何一步被取消都会直接结束，不补播断开音。紧急事件可抢占普通消息，静音和赛事切换清空旧队列，消息有效期同时覆盖合成与播放。

调用方停止等待后，迟到的合成结果或错误不能重新进入播放。公共输出最多保留两个进行中的合成调用；适配器忽略取消并占满额度时拒绝继续累积请求，由工程师隔离故障。播放器仍须履行取消契约，不能让旧音频与下一次播报重叠。

每个输出实例只在内存中缓存成功合成的 PCM，最多 32 条或 4 MiB，按进入顺序淘汰；不写音频缓存文件。缓存绑定该实例的声音设置，播放时应用当前音量；失败结果不缓存，释放时清空。缓存保存的是可复用声音，事件是否重播仍由赛事队列的去重和阶段规则决定。

## 原创无线电音与验证

「自定义接通/断开音」可分别选择音频和恢复默认，导入只发生在用户选文件之后。App 通过 `NAudio.Wasapi` 3.1.0／Windows Media Foundation 解码 WAV、MP3、M4A 和 FLAC（可用性取决于安装的系统解码器），限制源文件不超过 10 MiB、音频不超过 5 秒且最多双声道；解码读取输出时再次检查时长，统一重采样为 24 kHz 单声道 PCM16。公共语音核心不依赖 NAudio。空文件、异常格式、超限或取消不会覆盖现有设置；缺少解码器时仍可使用默认提示音。

接通音和断开音可独立开关；关闭某一段时也跳过它相邻的停顿，保留已导入的音频，语音正文照常播放。开关分别保存为 `raceEngineer.connectEnabled` 和 `raceEngineer.disconnectEnabled`，缺失或无效值默认开启；音色 ID 和开关均沿用 AppSettings，无需数据库迁移，旧应用忽略新增设置。每次播报固定一份开关快照，试听使用当前设置。

`CustomRadioCue` 将名称、版本 1 和 PCM 副本存为 `raceEngineer.connectCue`／`raceEngineer.disconnectCue` 两个独立 AppSettings 值，每次更新沿用 SQLite 的单条原子写入。不保存源文件路径，不在播报热路径读取文件；最多约 320 KB 的 Base64 PCM 加少量 JSON 元数据／项。现有设置备份包含该副本，无需数据库迁移；缺失字段沿用默认音，未知版本或损坏数据回退到默认并提示重新导入。恢复默认只清空对应设置，不删除用户原文件。旧应用忽略新增设置。

`RadioCues` 通过确定性算法合成约 205 ms 接通音和 155 ms 断开音：不等长双脉冲、轻微扫频、少量谐波和短促电台噪声尾音，边缘平滑淡入淡出。未使用电视转播录音或第三方提示音素材。

`RaceEngineerTests`、`SpeechPipelineTests` 和 `CustomRadioCueTests` 覆盖重复事件、冷却、紧急插队、静音、赛事切换、合成迟到、超时、停顿取消、队列与缓存上限、过期、资源释放、离线试听及示例选择、PCM／提示音边界和自定义音频的设置重启恢复。自动测试使用可控合成器和播放器，不要求安装特定语音或音频设备。Windows 文件解码、SAPI 声音、音频设备及听感在目标机器检查。
