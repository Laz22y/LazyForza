using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using LazyForza.Overlay;

namespace LazyForza.App;

internal sealed record AppLanguageOption(string Code, string NativeName, string EnglishName)
{
    public override string ToString() => NativeName;
}

internal static class AppLocalization
{
    private static readonly AppLanguageOption[] LanguageOptions =
    [
        new("zh-Hans", "简体中文", "Simplified Chinese"),
        new("en", "English", "English")
    ];

    private static IReadOnlyDictionary<string, string> translations =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, string> RaceServerLiteralTokens =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["lobby"] = "lobby",
            ["practice"] = "practice",
            ["qualifying"] = "qualifying",
            ["grid"] = "grid",
            ["outLap"] = "outLap",
            ["formationLap"] = "formationLap",
            ["countdown"] = "countdown",
            ["race"] = "race",
            ["finished"] = "finished",
            ["green"] = "green",
            ["yellow"] = "yellow",
            ["red"] = "red",
            ["chequered"] = "chequered",
            ["retired"] = "retired",
            ["disqualified"] = "disqualified",
            ["warningsOnly"] = "warningsOnly",
            ["automatic"] = "automatic",
            ["disabled"] = "disabled"
        };
    private static readonly HashSet<string> RaceServerIdentityGroups =
        new(StringComparer.Ordinal) { "name", "first", "second", "team" };
    private static readonly Regex ConfirmingStartPattern = new(
        "^正在确认起点 · (?<count>[0-9]+) 个轨迹点$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DriveThroughLapsPattern = new(
        "^还可跨越终点线 (?<count>[0-9]+) 次$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CandidateVerifiedPattern = new(
        "^候选：(?<name>.+) · 已验证 (?<meters>[0-9]+) m$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TrackCorrectionEvidencePattern = new(
        "^候选 (?<rank>[0-9]+) · 平均偏差 (?<mean>.+) · 路线进度 (?<progress>[0-9]+) m · 有效率 (?<ratio>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UpdateDownloadPattern = new(
        "^正在从 (?<source>.+) 下载更新…$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UpdateFallbackPattern = new(
        "^(?<source>.+) 下载或校验失败，正在切换到 (?<fallback>.+)…$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AutomaticRecordingBufferPattern = new(
        "^正在自动录制比赛；已包含赛前 (?<seconds>[0-9]+) 秒缓冲。$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AutomaticRecordingPostRollPattern = new(
        "^比赛结束，继续记录 (?<seconds>[0-9]+) 秒。$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AutomaticRecordingSavedPattern = new(
        "^自动录制已保存：(?<file>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StartSequenceCountdownPattern = new(
        "^(?<seconds>[0-9]+) 秒后启动发车程序$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly DynamicLiteralTemplate[] DynamicLiteralTemplates =
    [
        Template("^(?<reason>.+)，本圈已取消。$", "template.estateLapCancelled", "{0}，本圈已取消。", "reason"),
        Template("^本圈无效：(?<reason>.+)。$", "template.invalidEstateLap", "本圈无效：{0}。", "reason"),
        Template("^参考圈已录入：(?<seconds>.+) s。$", "template.referenceLapRecorded", "参考圈已录入：{0} s。", "seconds"),
        Template("^地产环道处理失败：(?<error>.+)$", "template.estateProcessingFailed", "地产环道处理失败：{0}", "error"),
        Template("^地产环道已保存：参考圈 (?<reference>.+) s，验证圈 (?<validation>.+) s。$", "template.estateSaved", "地产环道已保存：参考圈 {0} s，验证圈 {1} s。", "reference", "validation"),
        Template("^分段数必须在 (?<minimum>[0-9]+) 到 (?<maximum>[0-9]+) 之间。$", "template.sectorCountRange", "分段数必须在 {0} 到 {1} 之间。", "minimum", "maximum"),
        Template("^继续完成验证圈；需依次通过 (?<count>[0-9]+) 个检查点。$", "template.continueValidationLap", "继续完成验证圈；需依次通过 {0} 个检查点。", "count"),
        Template("^完成有效圈：(?<seconds>.+) s。$", "template.validEstateLap", "完成有效圈：{0} s。", "seconds"),
        Template("^维修区通道存在 (?<gap>.+) 米的采样断点，无法可靠生成维修区起终点门。请降低车速并完整重录维修区通道。$", "template.pitLaneSampleGap", "维修区通道存在 {0} 米的采样断点，无法可靠生成维修区起终点门。请降低车速并完整重录维修区通道。", "gap"),
        Template("^维修区通道录入失败：遥测轨迹中断 (?<gap>.+) 米。$", "template.pitLaneCaptureGap", "维修区通道录入失败：遥测轨迹中断 {0} 米。", "gap"),
        Template("^维修区通道已录入：(?<meters>.+) 米。$", "template.pitLaneRecorded", "维修区通道已录入：{0} 米。", "meters"),
        Template("^现有维修区通道存在 (?<gap>.+) 米的轨迹断点。请勾选“维修区通道”并完整重录后再保存。$", "template.existingPitLaneGap", "现有维修区通道存在 {0} 米的轨迹断点。请勾选“维修区通道”并完整重录后再保存。", "gap"),
        Template("^验证圈未通过：路线有效率 (?<ratio>.+)，检查点 (?<next>[0-9]+)/(?<total>[0-9]+)。$", "template.validationLapFailed", "验证圈未通过：路线有效率 {0}，检查点 {1}/{2}。", "ratio", "next", "total"),
        Template("^已恢复 (?<time>.+) 的录入暂存。$", "template.estateDraftRestored", "已恢复 {0} 的录入暂存。", "time"),
        Template("^已记录 (?<count>[0-9]+) 个换胎区边界点。$", "template.serviceCornersRecorded", "已记录 {0} 个换胎区边界点。", "count"),
        Template("^已完成计圈，但本圈不计入最快圈：(?<reason>.+)。$", "template.estateLapExcluded", "已完成计圈，但本圈不计入最快圈：{0}。", "reason"),
        Template("^正在录入维修区通道：(?<count>[0-9]+) 个样本。$", "template.pitLaneRecordingSamples", "正在录入维修区通道：{0} 个样本。", "count"),
        Template("^已载入维修区，仅重设：(?<scope>.+)。$", "template.pitScopeLoaded", "已载入维修区，仅重设：{0}。", "scope"),
        Template("^有效样本：第一次 (?<first>[0-9]+)，第二次 (?<second>[0-9]+)。保持低速并沿横线行驶。$", "template.finishTraceSamples", "有效样本：第一次 {0}，第二次 {1}。保持低速并沿横线行驶。", "first", "second"),
        Template("^赛道“(?<track>.+)”出现持续大幅偏离，程序已重新开始识别。$", "template.trackRematching", "赛道“{0}”出现持续大幅偏离，程序已重新开始识别。", "track"),
        Template("^圈速写入失败：(?<error>.+)$", "template.lapSaveFailed", "圈速写入失败：{0}", "error"),
        Template("^起点距离 (?<distance>.+) m 超出 (?<limit>.+) m$", "template.startDistanceRejected", "起点距离 {0} m 超出 {1} m", "distance", "limit"),
        Template("^连续三维投影不匹配（有效率 (?<ratio>.+)）$", "template.projectionMismatch", "连续三维投影不匹配（有效率 {0}）", "ratio"),
        Template("^路线进度 (?<progress>.+) m · 有效率 (?<ratio>.+)$", "template.routeProgressRatio", "路线进度 {0} m · 有效率 {1}", "progress", "ratio"),
        Template("^行驶方向相反（方向相似度 (?<similarity>.+)）$", "template.wrongDirectionSimilarity", "行驶方向相反（方向相似度 {0}）", "similarity"),
        Template("^需重新学习：(?<track>.+)$", "template.trackRelearnRequired", "需重新学习：{0}", "track"),
        Template("^学习赛道 (?<time>.+)$", "template.learningTrackName", "学习赛道 {0}", "time"),
        Template("^已纠正为“(?<track>.+)”。为避免保存不完整圈速，将从下次经过起点后开始记录。$", "template.trackCorrected", "已纠正为“{0}”。为避免保存不完整圈速，将从下次经过起点后开始记录。", "track"),
        Template("^用户将当前比赛赛道纠正为“(?<track>.+)”。$", "template.trackCorrectedLog", "用户将当前比赛赛道纠正为“{0}”。", "track"),
        Template("^早期曲率不匹配（方向变化差 (?<signed>.+) rad，曲率差 (?<absolute>.+) rad）$", "template.curvatureMismatch", "早期曲率不匹配（方向变化差 {0} rad，曲率差 {1} rad）", "signed", "absolute"),
        Template("^长度/进度范围不兼容（行驶 (?<travel>.+) m，路线进度 (?<progress>.+) m）$", "template.routeLengthMismatch", "长度/进度范围不兼容（行驶 {0} m，路线进度 {1} m）", "travel", "progress"),
        Template("^正在学习 · (?<seconds>.+) 秒 · (?<points>[0-9]+) 个轨迹点$", "template.learningTrackProgress", "正在学习 · {0} 秒 · {1} 个轨迹点", "seconds", "points"),
        Template("^当前轮胎周期还需要 (?<count>[0-9]+) 个干净圈。进站圈、赛道边界事件和异常离群圈不会用于趋势判断。$", "template.cleanLapsNeeded", "当前轮胎周期还需要 {0} 个干净圈。进站圈、赛道边界事件和异常离群圈不会用于趋势判断。", "count"),
        Template("^(?<summary>.*)本场至少要求 (?<minimum>[0-9]+) 次有效维修停留，目前还差 (?<remaining>[0-9]+) 次。$", "template.minimumPitStopsRemaining", "{0}本场至少要求 {1} 次有效维修停留，目前还差 {2} 次。", "summary", "minimum", "remaining"),
        Template("^本场至少要求 (?<minimum>[0-9]+) 次有效维修停留，目前还差 (?<remaining>[0-9]+) 次；窗口按规定进站次数与当前配速衰减共同计算。$", "template.pitWindowRequiredStops", "本场至少要求 {0} 次有效维修停留，目前还差 {1} 次；窗口按规定进站次数与当前配速衰减共同计算。", "minimum", "remaining"),
        Template("^本场至少要求 (?<minimum>[0-9]+) 次有效维修停留，目前还差 (?<remaining>[0-9]+) 次；若继续留在赛道，将没有足够圈数完成规定进站。$", "template.pitStopsUrgent", "本场至少要求 {0} 次有效维修停留，目前还差 {1} 次；若继续留在赛道，将没有足够圈数完成规定进站。", "minimum", "remaining"),
        Template("^当前还没有适合本车的历史样本。(?<phase>.*)$", "template.noVehicleStrategySamples", "当前还没有适合本车的历史样本。{0}", "phase"),
        Template("^建议第 (?<first>[0-9]+)–(?<last>[0-9]+) 圈末进站$", "template.suggestedPitWindow", "建议第 {0}–{1} 圈末进站", "first", "last"),
        Template("^已匹配 (?<count>[0-9]+) 条压缩样本（(?<description>.+)）。(?<phase>.*)$", "template.matchedStrategySamples", "已匹配 {0} 条压缩样本（{1}）。{2}", "count", "description", "phase"),
        Template("^与继续跑相比，一停方案预计可回收约 (?<seconds>.+) 秒；窗口内差异小于当前模型误差。$", "template.oneStopAdvantage", "与继续跑相比，一停方案预计可回收约 {0} 秒；窗口内差异小于当前模型误差。", "seconds"),
        Template("^在当前趋势下，一次额外进站尚不能稳定回收约 (?<seconds>.+) 秒损失；预测优势没有超过误差余量。$", "template.extraStopNotRecovered", "在当前趋势下，一次额外进站尚不能稳定回收约 {0} 秒损失；预测优势没有超过误差余量。", "seconds"),
        Template("^飞驰圈完成：(?<lap>.+)。 ?$", "template.flyingLapComplete", "飞驰圈完成：{0}。", "lap"),
        Template("^单圈无效：(?<reason>.+)。测试已终止。 ?$", "template.practiceInvalidLap", "单圈无效：{0}。测试已终止。", "reason"),
        Template("^连续完成 (?<count>[0-9]+) 个干净圈，建立本车在本赛道的配速衰退样本。$", "template.longRunTarget", "连续完成 {0} 个干净圈，建立本车在本赛道的配速衰退样本。", "count"),
        Template("^模拟换胎完成，记录的维修区总用时为 (?<seconds>.+) 秒。 ?$", "template.pitSimulationComplete", "模拟换胎完成，记录的维修区总用时为 {0} 秒。", "seconds"),
        Template("^项目成功：(?<message>.+) 请返回维修区，等待下一项安排。$", "template.practiceTestSucceeded", "项目成功：{0} 请返回维修区，等待下一项安排。", "message"),
        Template("^项目失败：(?<message>.+) 请返回维修区，确认车辆和赛道状态后可重新开始。$", "template.practiceTestFailed", "项目失败：{0} 请返回维修区，确认车辆和赛道状态后可重新开始。", "message"),
        Template("^已保存终止前的 (?<count>[0-9]+) 个完整干净圈；异常圈和未完成阶段未写入样本。$", "template.partialLongRunSaved", "已保存终止前的 {0} 个完整干净圈；异常圈和未完成阶段未写入样本。", "count"),
        Template("^已完成 (?<count>[0-9]+) 个连续干净圈，轮胎周期样本已保存。 ?$", "template.longRunComplete", "已完成 {0} 个连续干净圈，轮胎周期样本已保存。", "count"),
        Template("^长距离进行中：已完成 (?<done>[0-9]+)/(?<total>[0-9]+) 圈。不要进站、暂停、回转或越过赛道边界。$", "template.longRunProgress", "长距离进行中：已完成 {0}/{1} 圈。不要进站、暂停、回转或越过赛道边界。", "done", "total"),
        Template("^连接失败：(?<error>.+)$", "template.raceConnectionFailed", "连接失败：{0}", "error"),
        Template("^连接中断，(?<seconds>.+) 秒后进行第 (?<attempt>[0-9]+) 次重连…$", "template.raceReconnectDelay", "连接中断，{0} 秒后进行第 {1} 次重连…", "seconds", "attempt"),
        Template("^赛事连接尚未恢复：(?<error>.+)$", "template.raceReconnectFailed", "赛事连接尚未恢复：{0}", "error"),
        Template("^比赛方向采样必须从终点线前至少 (?<before>.+) 米开始，并在线后至少 (?<after>.+) 米结束；不要从终点线上直接开始采样。$", "template.directionCaptureSides", "比赛方向采样必须从终点线前至少 {0} 米开始，并在线后至少 {1} 米结束；不要从终点线上直接开始采样。", "before", "after"),
        Template("^拟合未通过：RMS (?<rms>.+) m，双向偏移 (?<offset>.+) m，角度差 (?<angle>.+)°。$", "template.finishGateFitFailed", "拟合未通过：RMS {0} m，双向偏移 {1} m，角度差 {2}°。", "rms", "offset", "angle"),
        Template("^终点门拟合通过：宽 (?<width>.+) m，RMS (?<rms>.+) m。$", "template.finishGateFitPassed", "终点门拟合通过：宽 {0} m，RMS {1} m。", "width", "rms"),
        Template("^无法识别发行版标签“(?<tag>.+)”。仅支持 v主版本.次版本.修订号。$", "template.releaseTagInvalid", "无法识别发行版标签“{0}”。仅支持 v主版本.次版本.修订号。", "tag"),
        Template("^(?<source>GitCode )?安装版升级程序大小异常：(?<size>.+) 字节。$", "template.installerSizeInvalid", "{0}安装版升级程序大小异常：{1} 字节。", "source", "size"),
        Template(@"^GitCode 安装版升级程序缺少 (?<file>.+)\.sha256，无法安全验证下载文件。$", "template.gitCodeInstallerChecksumMissing", "GitCode 安装版升级程序缺少 {0}.sha256，无法安全验证下载文件。", "file"),
        Template("^(?<source>GitCode )?发行版必须且只能包含一个预期文件 (?<file>.+)。$", "template.releaseExpectedFile", "{0}发行版必须且只能包含一个预期文件 {1}。", "source", "file"),
        Template(@"^GitCode 发行版缺少 (?<file>.+)\.sha256，无法安全验证下载文件。$", "template.gitCodeChecksumMissing", "GitCode 发行版缺少 {0}.sha256，无法安全验证下载文件。", "file"),
        Template("^(?<source>GitCode )?发行版文件大小异常：(?<size>.+) 字节。$", "template.releaseFileSizeInvalid", "{0}发行版文件大小异常：{1} 字节。", "source", "size"),
        Template("^(?<source>GitCode|GitHub) 返回了 HTTP (?<status>[0-9]+)，暂时无法检查更新。$", "template.updateCheckHttpError", "{0} 返回了 HTTP {1}，暂时无法检查更新。", "source", "status"),
        Template("^(?<primary>.+) 更新下载失败，(?<alternate>.+) 备用源也无法取得发行版信息。$", "template.updateFallbackUnavailable", "{0} 更新下载失败，{1} 备用源也无法取得发行版信息。", "primary", "alternate"),
        Template("^(?<primary>.+) 更新下载失败，(?<alternate>.+) 当前发行版不是同一版本 (?<version>.+)。$", "template.updateFallbackVersionMismatch", "{0} 更新下载失败，{1} 当前发行版不是同一版本 {2}。", "primary", "alternate", "version"),
        Template("^不支持的更新来源：(?<source>.+)。$", "template.updateSourceUnsupported", "不支持的更新来源：{0}。", "source"),
        Template("^更新包内部文件校验失败：(?<file>.+)$", "template.updateInternalFileInvalid", "更新包内部文件校验失败：{0}", "file"),
        Template("^更新包缺少清单文件：(?<file>.+)$", "template.updateManifestFileMissing", "更新包缺少清单文件：{0}", "file"),
        Template("^(?<source>.+) 发行版缺少 SHA-256 校验信息。$", "template.releaseChecksumMissing", "{0} 发行版缺少 SHA-256 校验信息。", "source"),
        Template("^(?<source>.+) 发行版缺少安装版升级程序。$", "template.releaseInstallerMissing", "{0} 发行版缺少安装版升级程序。", "source"),
        Template("^(?<source>.+) 返回的更新包大小与发行版信息不一致。$", "template.updateSizeMismatch", "{0} 返回的更新包大小与发行版信息不一致。", "source"),
        Template("^(?<source>.+) 更新包 SHA-256 校验失败，文件可能不完整或已被篡改。$", "template.updateChecksumFailed", "{0} 更新包 SHA-256 校验失败，文件可能不完整或已被篡改。", "source"),
        Template("^发行版来源为 (?<releaseSource>.+)，不能交给 (?<clientSource>.+) 下载器处理。$", "template.releaseSourceMismatch", "发行版来源为 {0}，不能交给 {1} 下载器处理。", "releaseSource", "clientSource"),
        Template("^发行版文件 (?<file>.+) 的下载地址无效。$", "template.releaseDownloadInvalid", "发行版文件 {0} 的下载地址无效。", "file"),
        Template("^更新包下载不完整：预期 (?<expected>.+) 字节，实际 (?<actual>.+) 字节。$", "template.updateDownloadIncomplete", "更新包下载不完整：预期 {0} 字节，实际 {1} 字节。", "expected", "actual"),
        Template("^连接 (?<source>.+) 下载更新超时。$", "template.updateDownloadTimeout", "连接 {0} 下载更新超时。", "source"),
        Template("^连接 (?<source>.+) 下载更新失败。$", "template.updateDownloadFailed", "连接 {0} 下载更新失败。", "source"),
        Template("^连接 (?<source>.+) 下载校验和失败。$", "template.updateChecksumDownloadFailed", "连接 {0} 下载校验和失败。", "source"),
        Template("^下载更新时 (?<source>.+) 返回了 HTTP (?<status>[0-9]+)。$", "template.updateDownloadHttpError", "下载更新时 {0} 返回了 HTTP {1}。", "source", "status"),
        Template("^下载校验和时 (?<source>.+) 返回了 HTTP (?<status>[0-9]+)。$", "template.updateChecksumHttpError", "下载校验和时 {0} 返回了 HTTP {1}。", "source", "status"),
        Template("^备份包包含未列入清单的文件：(?<file>.+)。$", "template.backupUnexpectedFile", "备份包包含未列入清单的文件：{0}。", "file"),
        Template("^备份包含未知数据表：(?<table>.+)。$", "template.backupUnknownTable", "备份包含未知数据表：{0}。", "table"),
        Template("^备份包含重复数据表：(?<table>.+)。$", "template.backupDuplicateTable", "备份包含重复数据表：{0}。", "table"),
        Template("^备份包缺少清单文件：(?<file>.+)。$", "template.backupManifestFileMissing", "备份包缺少清单文件：{0}。", "file"),
        Template("^备份表结构不匹配：(?<table>.+)。$", "template.backupTableSchemaMismatch", "备份表结构不匹配：{0}。", "table"),
        Template("^备份表行长度不匹配：(?<table>.+)。$", "template.backupTableRowMismatch", "备份表行长度不匹配：{0}。", "table"),
        Template("^备份文件过大：(?<file>.+)。$", "template.backupFileTooLarge", "备份文件过大：{0}。", "file"),
        Template("^备份文件校验失败：(?<file>.+)。$", "template.backupFileChecksumFailed", "备份文件校验失败：{0}。", "file"),
        Template("^不支持的备份格式版本：(?<version>.+)。$", "template.backupVersionUnsupported", "不支持的备份格式版本：{0}。", "version"),
        Template("^无法读取备份文件：(?<file>.+)。$", "template.backupFileUnreadable", "无法读取备份文件：{0}。", "file"),
        Template("^(?<file>.+) 超过大小限制。$", "template.fileTooLarge", "{0} 超过大小限制。", "file"),
        Template("^(?<file>.+) 解压后超过大小限制。$", "template.extractedFileTooLarge", "{0} 解压后超过大小限制。", "file"),
        Template("^地产环道的(?<name>.+)定义无效。$", "template.estateDefinitionInvalid", "地产环道的{0}定义无效。", "name"),
        Template("^地产环道的(?<name>.+)宽度无效。$", "template.estateWidthInvalid", "地产环道的{0}宽度无效。", "name"),
        Template("^不支持的圈速分析数据版本：(?<version>.+)。$", "template.lapExchangeSchemaUnsupported", "不支持的圈速分析数据版本：{0}。", "version"),
        Template("^不支持的圈速分析文件版本：(?<version>.+)。$", "template.lapExchangeContainerUnsupported", "不支持的圈速分析文件版本：{0}。", "version"),
        Template("^圈速分析文件必须包含 1–(?<maximum>[0-9]+) 圈。$", "template.lapExchangeLapCount", "圈速分析文件必须包含 1–{0} 圈。", "maximum"),
        Template("^车辆 (?<ordinal>[0-9]+)$", "template.vehicleOrdinal", "车辆 {0}", "ordinal"),
        Template("^(?<count>[0-9]+) 圈未计入稳定性统计，可结合无效原因复查。$", "template.reviewExcludedLaps", "{0} 圈未计入稳定性统计，可结合无效原因复查。", "count"),
        Template("^第 (?<sector>[0-9]+) 段波动最大，标准差 (?<seconds>.+) 秒。$", "template.reviewUnstableSector", "第 {0} 段波动最大，标准差 {1} 秒。", "sector", "seconds"),
        Template("^末圈比首个有效圈快 (?<seconds>.+) 秒，比赛中仍在持续改善。$", "template.reviewImproved", "末圈比首个有效圈快 {0} 秒，比赛中仍在持续改善。", "seconds"),
        Template("^末圈比首个有效圈慢 (?<seconds>.+) 秒，可留意后程失误或节奏下降。$", "template.reviewSlower", "末圈比首个有效圈慢 {0} 秒，可留意后程失误或节奏下降。", "seconds"),
        Template("^圈速波动较小，标准差为 (?<seconds>.+) 秒。$", "template.reviewConsistent", "圈速波动较小，标准差为 {0} 秒。", "seconds"),
        Template("^整体节奏基本稳定，圈速标准差为 (?<seconds>.+) 秒。$", "template.reviewMostlyConsistent", "整体节奏基本稳定，圈速标准差为 {0} 秒。", "seconds"),
        Template("^组合本场各段最快约还能缩短 (?<seconds>.+) 秒。$", "template.reviewPotential", "组合本场各段最快约还能缩短 {0} 秒。", "seconds")
    ];

    private static readonly DynamicLiteralTemplate[] RaceServerLiteralTemplates =
    [
        Template("^(?<name>.+) 进入房间。$", "server.event.driverJoined", "{0} 进入房间。", "name"),
        Template("^(?<name>.+) 离开房间。$", "server.event.driverLeft", "{0} 离开房间。", "name"),
        Template("^OB (?<name>.+) 加入转播席。$", "server.event.observerJoined", "OB {0} 加入转播席。", "name"),
        Template("^OB (?<name>.+) 重新连接。$", "server.event.observerResumed", "OB {0} 重新连接。", "name"),
        Template("^OB (?<name>.+) 断开连接。$", "server.event.observerDisconnected", "OB {0} 断开连接。", "name"),
        Template("^(?<name>.+)已准备。$", "server.event.driverReady", "{0}已准备。", "name"),
        Template("^(?<name>.+)取消准备。$", "server.event.driverNotReady", "{0}取消准备。", "name"),
        Template("^(?<name>.+) 进入维修区。$", "server.event.pitEntered", "{0} 进入维修区。", "name"),
        Template("^(?<name>.+) 停入换胎区。$", "server.event.pitBoxEntered", "{0} 停入换胎区。", "name"),
        Template("^(?<name>.+) 完成换胎停留。$", "server.event.pitServiceCompleted", "{0} 完成换胎停留。", "name"),
        Template("^(?<name>.+) 离开维修区。$", "server.event.pitExited", "{0} 离开维修区。", "name"),
        Template("^(?<name>.+) 完成第 (?<lap>[0-9]+) 圈：(?<time>.+)（不计最快圈）。$", "server.event.lapCompletedIneligible", "{0} 完成第 {1} 圈：{2}（不计最快圈）。", "name", "lap", "time"),
        Template("^(?<name>.+) 完成第 (?<lap>[0-9]+) 圈：(?<time>.+)。$", "server.event.lapCompleted", "{0} 完成第 {1} 圈：{2}。", "name", "lap", "time"),
        Template("^(?<name>.+) 的本圈无效：(?<reason>.+)。$", "server.event.lapInvalid", "{0} 的本圈无效：{1}。", "name", "reason"),
        Template("^(?<name>.+) 率先完成预定圈数，方格旗生效。$", "server.event.chequered", "{0} 率先完成预定圈数，方格旗生效。", "name"),
        Template("^(?<name>.+) 率先完成 (?<laps>[0-9]+) 圈$", "server.banner.chequeredDetail", "{0} 率先完成 {1} 圈", "name", "laps"),
        Template("^(?<name>.+) 的换胎停留已补传确认（第 (?<count>[0-9]+) 次）。$", "server.event.pitRecovered", "{0} 的换胎停留已补传确认（第 {1} 次）。", "name", "count"),
        Template("^(?<name>.+) 完成第 (?<count>[0-9]+) 次换胎停留。$", "server.event.pitCompleted", "{0} 完成第 {1} 次换胎停留。", "name", "count"),
        Template("^(?<name>.+) 触发第 (?<sector>[0-9]+) 分段自动黄旗：(?<reason>.+)。$", "server.event.automaticYellow", "{0} 触发第 {1} 分段自动黄旗：{2}。", "name", "sector", "reason"),
        Template("^(?<name>.+) 的异常状态已恢复，自动黄旗解除。$", "server.event.automaticYellowCleared", "{0} 的异常状态已恢复，自动黄旗解除。", "name"),
        Template("^房间设置已更新，赛道边界处理为 (?<mode>.+)。$", "server.event.roomSettings", "房间设置已更新，赛道边界处理为 {0}。", "mode"),
        Template("^赛事阶段切换为 (?<phase>.+)。$", "server.event.phaseChanged", "赛事阶段切换为 {0}。", "phase"),
        Template("^赛事总控发布第 (?<sector>[0-9]+) 分段 (?<flag>.+)：(?<reason>.+)。$", "server.event.sectorFlag", "赛事总控发布第 {0} 分段 {1}：{2}。", "sector", "flag", "reason"),
        Template("^赛事总控发布全场 (?<flag>.+)。$", "server.event.fullCourseFlag", "赛事总控发布全场 {0}。", "flag"),
        Template("^赛事总控恢复第 (?<sector>[0-9]+) 分段绿旗。$", "server.event.sectorGreen", "赛事总控恢复第 {0} 分段绿旗。", "sector"),
        Template("^赛事总控断开了 (?<name>.+)，显示名称已释放。$", "server.event.controlDisconnected", "赛事总控断开了 {0}，显示名称已释放。", "name"),
        Template("^赛事总控断开了 OB (?<name>.+)，显示名称已释放。$", "server.event.controlDisconnectedObserver", "赛事总控断开了 OB {0}，显示名称已释放。", "name"),
        Template("^(?<name>.+) 状态改为 (?<status>.+)：(?<reason>.+)。$", "server.event.driverStatus", "{0} 状态改为 {1}：{2}。", "name", "status", "reason"),
        Template("^(?<name>.+) 开始执行 (?<seconds>.+) 秒停车罚时。$", "server.event.penaltyServiceStarted", "{0} 开始执行 {1} 秒停车罚时。", "name", "seconds"),
        Template("^(?<name>.+) 已完成 (?<seconds>.+) 秒停车罚时，可以开始换胎。$", "server.event.penaltyServiceCompleted", "{0} 已完成 {1} 秒停车罚时，可以开始换胎。", "name", "seconds"),
        Template("^(?<name>.+) 已完成通过维修区处罚。$", "server.event.driveThroughServed", "{0} 已完成通过维修区处罚。", "name"),
        Template("^(?<name>.+) 的通过维修区处罚还可跨越终点线 (?<count>[0-9]+) 次。$", "server.event.driveThroughReminder", "{0} 的通过维修区处罚还可跨越终点线 {1} 次。", "name", "count"),
        Template("^(?<name>.+) 必须在本圈结束前进入维修区执行通过维修区处罚。$", "server.event.driveThroughDue", "{0} 必须在本圈结束前进入维修区执行通过维修区处罚。", "name"),
        Template("^(?<name>.+) 未按期执行通过维修区处罚，原处罚已替换为 (?<seconds>.+) 秒完赛加时。$", "server.event.driveThroughOverdue", "{0} 未按期执行通过维修区处罚，原处罚已替换为 {1} 秒完赛加时。", "name", "seconds"),
        Template("^(?<name>.+) 完赛时只完成 (?<done>[0-9]+)/(?<required>[0-9]+) 次有效维修停留，判定未满足完赛条件。$", "server.event.minimumPitStopsMissed", "{0} 完赛时只完成 {1}/{2} 次有效维修停留，判定未满足完赛条件。", "name", "done", "required"),
        Template("^(?<name>.+) 的补传换胎停留已确认，最低进站要求恢复为已完成。$", "server.event.minimumPitStopsRecovered", "{0} 的补传换胎停留已确认，最低进站要求恢复为已完成。", "name"),
        Template("^(?<first>.+) 与 (?<second>.+) 发生疑似车辆接触，已交由总控调查（第 (?<lap>[0-9]+) 圈）。$", "server.event.collisionInvestigation", "{0} 与 {1} 发生疑似车辆接触，已交由总控调查（第 {2} 圈）。", "first", "second", "lap"),
        Template("^连续疑似车辆接触（(?<count>[0-9]+) 次，(?<seconds>.+) 秒内）$", "server.collision.repeated", "连续疑似车辆接触（{0} 次，{1} 秒内）", "count", "seconds"),
        Template("^偏离参考路线 (?<offset>.+) 米，估算获得约 (?<gain>.+) 米距离优势$", "server.trackLimits.deviation", "偏离参考路线 {0} 米，估算获得约 {1} 米距离优势", "offset", "gain"),
        Template("^维修区超速：(?<speed>.+) km/h，限速 (?<limit>.+) km/h$", "server.pit.speeding", "维修区超速：{0} km/h，限速 {1} km/h", "speed", "limit"),
        Template("^(?<team>.+) 已达到每队 (?<count>[0-9]+) 人上限。$", "server.error.teamFull", "{0} 已达到每队 {1} 人上限。", "team", "count"),
        Template("^房间人数已达到 (?<count>[0-9]+) 人上限。$", "server.error.roomFull", "房间人数已达到 {0} 人上限。", "count"),
        Template("^本场已达到 (?<count>[0-9]+) 人上限。$", "server.error.eventFull", "本场已达到 {0} 人上限。", "count"),
        Template("^OB 席位已达到 (?<count>[0-9]+) 人上限。$", "server.error.observerFull", "OB 席位已达到 {0} 人上限。", "count"),
        Template("^客户端赛道为 (?<client>[0-9]+) 个分段，房间设置为 (?<room>[0-9]+) 个分段。$", "server.error.sectorMismatch", "客户端赛道为 {0} 个分段，房间设置为 {1} 个分段。", "client", "room"),
        Template("^首盏红灯将在 (?<seconds>[0-9]+) 秒后亮起$", "server.banner.firstLight", "首盏红灯将在 {0} 秒后亮起", "seconds"),
        Template("^(?<count>[0-9]+) 名车手可完成已经开始的最后一圈$", "server.banner.finalLaps", "{0} 名车手可完成已经开始的最后一圈", "count"),
        Template("^(?<session>.+) 计时结束，等待 (?<count>[0-9]+) 名车手完成最后一圈。$", "server.event.waitingFinalLaps", "{0} 计时结束，等待 {1} 名车手完成最后一圈。", "session", "count"),
        Template("^(?<session>.+) 计时结束，成绩已冻结。$", "server.event.resultsFrozen", "{0} 计时结束，成绩已冻结。", "session"),
        Template("^(?<session>.+) 计时结束，本节成绩已冻结。$", "server.event.sessionResultsFrozen", "{0} 计时结束，本节成绩已冻结。", "session"),
        Template("^警告 · (?<reason>.+)$", "server.penalty.warning", "警告 · {0}", "reason"),
        Template("^待执行 \\+(?<seconds>.+) 秒 · (?<reason>.+)$", "server.penalty.time", "待执行 +{0} 秒 · {1}", "seconds", "reason"),
        Template("^通过维修区处罚 · (?<reason>.+)$", "server.penalty.driveThrough", "通过维修区处罚 · {0}", "reason"),
        Template("^停车 (?<seconds>.+) 秒 · (?<reason>.+)$", "server.penalty.stopAndGo", "停车 {0} 秒 · {1}", "seconds", "reason"),
        Template("^退后 (?<places>[0-9]+) 个发车位 · (?<reason>.+)$", "server.penalty.gridDrop", "退后 {0} 个发车位 · {1}", "places", "reason"),
        Template("^取消比赛资格 · (?<reason>.+)$", "server.penalty.disqualification", "取消比赛资格 · {0}", "reason"),
        Template("^(?<name>.+) 加入赛事。$", "server.event.joinedEvent", "{0} 加入赛事。", "name"),
        Template("^(?<name>.+) 重新连接。$", "server.event.reconnected", "{0} 重新连接。", "name"),
        Template("^(?<name>.+) 断开连接。$", "server.event.disconnected", "{0} 断开连接。", "name"),
        Template("^(?<name>.+) 完成有效圈 (?<time>.+)。$", "server.event.validLap", "{0} 完成有效圈 {1}。", "name", "time"),
        Template("^(?<name>.+) 完成计圈 (?<time>.+)，因赛道边界事件不计入最快圈。$", "server.event.lapExcluded", "{0} 完成计圈 {1}，因赛道边界事件不计入最快圈。", "name", "time"),
        Template("^(?<name>.+) 完成无效圈：(?<reason>.+)。$", "server.event.invalidLap", "{0} 完成无效圈：{1}。", "name", "reason"),
        Template("^(?<name>.+) 的异常状态已解除。$", "server.event.automaticHazardCleared", "{0} 的异常状态已解除。", "name"),
        Template("^(?<name>.+) 的未执行停车并通过维修区处罚已折算为 \\+(?<seconds>.+) 秒完赛加时。$", "server.event.stopAndGoConverted", "{0} 的未执行停车并通过维修区处罚已折算为 +{1} 秒完赛加时。", "name", "seconds"),
        Template("^(?<name>.+) 未正确执行 (?<seconds>.+) 秒停车罚时，处罚已转为通过维修区。$", "server.event.timePenaltyConverted", "{0} 未正确执行 {1} 秒停车罚时，处罚已转为通过维修区。", "name", "seconds"),
        Template("^(?<name>.+) 未正确执行 (?<seconds>.+) 秒停车罚时；比赛已进入最后三圈，处罚替换为 20 秒完赛加时。$", "server.event.timePenaltyLateConverted", "{0} 未正确执行 {1} 秒停车罚时；比赛已进入最后三圈，处罚替换为 20 秒完赛加时。", "name", "seconds"),
        Template("^(?<name>.+) 在红灯熄灭前移动，自动加罚 5 秒。$", "server.event.falseStart", "{0} 在红灯熄灭前移动，自动加罚 5 秒。", "name"),
        Template("^(?<name>.+) 抢跑，记录 5 秒待执行罚时。$", "server.event.falseStartRecorded", "{0} 抢跑，记录 5 秒待执行罚时。", "name"),
        Template("^(?<name>.+) 执行通过维修区处罚时停车，本次进站无效。$", "server.event.driveThroughStopped", "{0} 执行通过维修区处罚时停车，本次进站无效。", "name"),
        Template("^(?<name>.+) 执行通过维修区处罚时暂停或回转，本次进站无效。$", "server.event.driveThroughInterrupted", "{0} 执行通过维修区处罚时暂停或回转，本次进站无效。", "name"),
        Template("^(?<name>.+) 正在接受调查：(?<offense>.+)（第 (?<lap>[0-9]+) 圈）。$", "server.event.underInvestigation", "{0} 正在接受调查：{1}（第 {2} 圈）。", "name", "offense", "lap"),
        Template("^(?<name>.+) 被标记为 (?<status>.+)：(?<reason>.+)。$", "server.event.markedStatus", "{0} 被标记为 {1}：{2}。", "name", "status", "reason"),
        Template("^(?<session>.+) 计时结束，(?<count>[0-9]+) 名车手仍可完成最后飞驰圈。$", "server.event.qualifyingFinalLaps", "{0} 计时结束，{1} 名车手仍可完成最后飞驰圈。", "session", "count"),
        Template("^(?<session>.+) 计时结束，(?<count>[0-9]+) 名车手仍可完成最后一圈。$", "server.event.practiceFinalLaps", "{0} 计时结束，{1} 名车手仍可完成最后一圈。", "session", "count"),
        Template("^(?<count>[0-9]+) 名车手可完成最后飞驰圈$", "server.banner.qualifyingFinalLaps", "{0} 名车手可完成最后飞驰圈", "count"),
        Template("^服务端协议版本为 (?<version>[0-9]+)。$", "server.error.protocolVersion", "服务端协议版本为 {0}。", "version"),
        Template("^不支持消息类型：(?<type>.+)$", "server.error.unsupportedMessage", "不支持消息类型：{0}", "type"),
        Template("^(?<team>.+) 的代表色不是有效的 #RRGGBB 颜色。$", "server.error.teamColor", "{0} 的代表色不是有效的 #RRGGBB 颜色。", "team"),
        Template("^已开启车队，请完整配置 (?<count>[0-9]+) 支车队的名称和代表色。$", "server.error.teamConfiguration", "已开启车队，请完整配置 {0} 支车队的名称和代表色。", "count"),
        Template("^处罚 · (?<name>.+)$", "server.banner.penalty", "处罚 · {0}", "name"),
        Template("^调查结论 · (?<name>.+)$", "server.banner.investigationDecision", "调查结论 · {0}", "name"),
        Template("^罚时执行失败 · (?<name>.+)$", "server.banner.penaltyServiceFailed", "罚时执行失败 · {0}", "name"),
        Template("^抢跑 · (?<name>.+)$", "server.banner.falseStart", "抢跑 · {0}", "name"),
        Template("^赛道边界警告 · (?<name>.+)$", "server.banner.trackLimitWarning", "赛道边界警告 · {0}", "name"),
        Template("^通过维修区处罚逾期 · (?<name>.+)$", "server.banner.driveThroughOverdue", "通过维修区处罚逾期 · {0}", "name"),
        Template("^正在调查 · (?<name>.+)$", "server.banner.investigating", "正在调查 · {0}", "name"),
        Template("^自动判罚 · (?<name>.+)$", "server.banner.automaticPenalty", "自动判罚 · {0}", "name"),
        Template("^停车罚时执行失败：(?<reason>.+)$", "server.penalty.serviceFailed", "停车罚时执行失败：{0}", "reason"),
        Template("^通过维修区处罚未按期执行：(?<reason>.+)$", "server.penalty.driveThroughLate", "通过维修区处罚未按期执行：{0}", "reason"),
        Template("^未完成规定的最少有效维修停留次数（(?<done>[0-9]+)/(?<required>[0-9]+)）。$", "server.penalty.minimumPitStops", "未完成规定的最少有效维修停留次数（{0}/{1}）。", "done", "required")
    ];

    public static string CurrentLanguage { get; private set; } = StartupProfile.DefaultLanguage;

    public static IReadOnlyList<AppLanguageOption> SupportedLanguages => LanguageOptions;

    public static bool IsSupported(string? language) =>
        LanguageOptions.Any(option => option.Code.Equals(language, StringComparison.OrdinalIgnoreCase));

    public static void UseLanguage(string? language)
    {
        var option = LanguageOptions.FirstOrDefault(item =>
            item.Code.Equals(language, StringComparison.OrdinalIgnoreCase)) ?? LanguageOptions[0];
        CurrentLanguage = option.Code;
        translations = LoadTranslations(option.Code);
        OverlayTextLocalization.Configure(Literal);
        var culture = option.Code == "en" ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("zh-CN");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    public static string Text(string key, string chineseFallback)
    {
        if (CurrentLanguage == StartupProfile.DefaultLanguage) return chineseFallback;
        return translations.TryGetValue(key, out var localized) && !string.IsNullOrWhiteSpace(localized)
            ? localized
            : chineseFallback;
    }

    public static string Format(string key, string chineseFallback, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Text(key, chineseFallback), arguments);

    public static string Literal(string text)
    {
        if (string.IsNullOrEmpty(text) || CurrentLanguage == StartupProfile.DefaultLanguage) return text;
        if (translations.TryGetValue($"literal:{text}", out var localized) &&
            !string.IsNullOrWhiteSpace(localized))
            return localized;
        if (RaceServerLiteralTokens.TryGetValue(text, out var canonicalToken) &&
            translations.TryGetValue($"literal:{canonicalToken}", out localized) &&
            !string.IsNullOrWhiteSpace(localized))
            return localized;
        if (!text.Any(character => character is >= '\u3400' and <= '\u9FFF')) return text;
        var match = ConfirmingStartPattern.Match(text);
        if (match.Success)
            return Format(
                "template.confirmingStart",
                "正在确认起点 · {0} 个轨迹点",
                match.Groups["count"].Value);
        match = DriveThroughLapsPattern.Match(text);
        if (match.Success)
            return Format(
                "template.driveThroughLaps",
                "还可跨越终点线 {0} 次",
                match.Groups["count"].Value);
        match = CandidateVerifiedPattern.Match(text);
        if (match.Success)
            return Format(
                "template.candidateVerified",
                "候选：{0} · 已验证 {1} m",
                match.Groups["name"].Value,
                match.Groups["meters"].Value);
        match = TrackCorrectionEvidencePattern.Match(text);
        if (match.Success)
            return Format(
                "template.trackCorrectionEvidence",
                "候选 {0} · 平均偏差 {1} · 路线进度 {2} m · 有效率 {3}",
                match.Groups["rank"].Value,
                match.Groups["mean"].Value,
                match.Groups["progress"].Value,
                match.Groups["ratio"].Value);
        match = UpdateDownloadPattern.Match(text);
        if (match.Success)
            return Format(
                "template.updateDownload",
                "正在从 {0} 下载更新…",
                match.Groups["source"].Value);
        match = UpdateFallbackPattern.Match(text);
        if (match.Success)
            return Format(
                "template.updateFallback",
                "{0} 下载或校验失败，正在切换到 {1}…",
                match.Groups["source"].Value,
                match.Groups["fallback"].Value);
        match = AutomaticRecordingBufferPattern.Match(text);
        if (match.Success)
            return Format(
                "template.automaticRecordingBuffer",
                "正在自动录制比赛；已包含赛前 {0} 秒缓冲。",
                match.Groups["seconds"].Value);
        match = AutomaticRecordingPostRollPattern.Match(text);
        if (match.Success)
            return Format(
                "template.automaticRecordingPostRoll",
                "比赛结束，继续记录 {0} 秒。",
                match.Groups["seconds"].Value);
        match = AutomaticRecordingSavedPattern.Match(text);
        if (match.Success)
            return Format(
                "template.automaticRecordingSaved",
                "自动录制已保存：{0}",
                match.Groups["file"].Value);
        match = StartSequenceCountdownPattern.Match(text);
        if (match.Success)
            return Format(
                "template.startSequenceCountdown",
                "{0} 秒后启动发车程序",
                match.Groups["seconds"].Value);
        foreach (var template in DynamicLiteralTemplates.Concat(RaceServerLiteralTemplates))
        {
            match = template.Pattern.Match(text);
            if (!match.Success) continue;
            var arguments = template.Groups
                .Select(group => (object?)(template.Key.StartsWith("server.", StringComparison.Ordinal) &&
                                             RaceServerIdentityGroups.Contains(group)
                    ? match.Groups[group].Value
                    : Literal(match.Groups[group].Value)))
                .ToArray();
            return Format(template.Key, template.Fallback, arguments);
        }
        return text;
    }

    private static DynamicLiteralTemplate Template(
        string pattern,
        string key,
        string fallback,
        params string[] groups) =>
        new(
            new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant),
            key,
            fallback,
            groups);

    public static void ApplyTo(DependencyObject root)
    {
        if (CurrentLanguage == StartupProfile.DefaultLanguage) return;
        if (root is Window window)
            window.Title = Literal(window.Title);
        if (root is TextBlock textBlock)
            textBlock.Text = Literal(textBlock.Text);
        if (root is ContentControl { Content: string content } contentControl)
            contentControl.Content = Literal(content);
        if (root is HeaderedContentControl { Header: string header } headered)
            headered.Header = Literal(header);
        if (root is ItemsControl { ItemsSource: null } itemsControl)
        {
            for (var index = 0; index < itemsControl.Items.Count; index++)
            {
                if (itemsControl.Items[index] is string item)
                    itemsControl.Items[index] = Literal(item);
            }
        }

        if (root is FrameworkElement element && element.ToolTip is string tooltip)
            element.ToolTip = Literal(tooltip);
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            ApplyTo(child);
    }

    private static IReadOnlyDictionary<string, string> LoadTranslations(string language)
    {
        if (language == StartupProfile.DefaultLanguage)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var assembly = typeof(AppLocalization).Assembly;
        var suffix = $".Localization.{language}.json";
        var resourceName = assembly.GetManifestResourceNames().SingleOrDefault(name =>
            name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null) return new Dictionary<string, string>(StringComparer.Ordinal);
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return new Dictionary<string, string>(StringComparer.Ordinal);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ??
               new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private sealed record DynamicLiteralTemplate(
        Regex Pattern,
        string Key,
        string Fallback,
        IReadOnlyList<string> Groups);
}
