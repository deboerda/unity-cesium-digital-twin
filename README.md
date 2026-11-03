# 飞行可视化

基于 Unity 2022.3 的编队飞行与传感器扫描范围可视化工程。二维地图与三维场景共用同一套位置、姿态和传感器数据。

当前版本 **1.2**。仓库： [unity-cesium-digital-twin](https://github.com/deboerda/unity-cesium-digital-twin)。

## 环境

- Unity 2022.3.62f3（或同系列 2022.3）
- 内置渲染管线
- 仓库已包含 Codely Bridge 1.0.80（`.codely.packages/`）

## 打开工程

1. Unity Hub 打开本仓库根目录（含 `Assets/`、`Packages/`、`.codely.packages/`）。
2. Package Manager 中应能看到 **Codely Bridge**（路径 `file:../.codely.packages/cn.tuanjie.codely.bridge@1.0.80`）。
3. 打开 `Assets/Scenes/HK132.unity`。
4. 先退出 Play，再进入运行模式。

## 视图

| 视图 | 操作 |
|---|---|
| 总览（二维） | 滚轮缩放，拖拽平移 |
| 飞行（三维） | 画面上 WASD/QE 移动，滚轮推拉，左键环绕，右键飞行，中键平移；**F** 框住编队 |

二维缩放/平移、三维相机位置、以及上次停留的视图，都会写入面板记忆，下次进入运行模式会恢复。

三维使用高空渐变天空盒（上为天空、下为远处地面），不用贴地实景。姿态窗口只显示机模。

## 数据显示

- 飞机位置：经度、纬度、高度、航向、俯仰、横滚
- 传感器扫描体：二维扇面与三维锥体/球体，按同一套角度判定
- 航迹：二维与三维共用采样间隔和最大点数（F4 可调）
- 状态信息：编号、时间、状态、提示文本

## 多传感器数据

事件数据通过 `data.detectors[]` 驱动同一架飞机上的多台传感器。名称字段为 **`detector_name`**，须与数据端配置的名称完全一致。不要使用 CSV 列前缀作为名称。

每台传感器 6 个字段：

| 字段 | 说明 |
|---|---|
| `detector_name` | 名称，身份键为 `飞机编号\|detector_name` |
| `center_point` | 安装位置：机头 / 中心 / 机尾（空则中心） |
| `yaw_angle` | 左右偏转角（度） |
| `pitch_angle` | 上下偏转角（度） |
| `yaw_scan_angle` | 左右扫描总角（度） |
| `pitch_scan_angle` | 上下扫描总角（度） |

示例：

```json
{
  "type": "EVENT_TABLE_DATA",
  "data": {
    "flightTime": "2026-10-27 08:00:01.000",
    "trackId": "UAV-001",
    "detectors": [
      {
        "detector_name": "雷达",
        "center_point": "机头",
        "yaw_angle": 7.7,
        "pitch_angle": 1.6,
        "yaw_scan_angle": 120,
        "pitch_scan_angle": 40
      }
    ]
  }
}
```

扫描距离以软件内传感器面板配置为准，数组中不必带距离字段。本帧未出现的传感器不会被更新；面板中已删除的条目不会被数据重新创建。

## 运行时面板

| 快捷键 | 作用 |
|---|---|
| F2 | 传感器安装校准 |
| F3 | 三维差值标签 |
| F4 | 显示大小（飞机、航迹、扫描环、三维机模等） |

F4 中「三维机模」范围 0.2–80，按保存值生效；「环粗细」下限 0.02。点 **保存显示大小** 写入记忆。

## Codely Bridge（可选）

用于编辑器与外部工具（截图、选中资源）通信。

1. 菜单 **Tools > Codely Bridge > Control Window**，确认服务已启动。
2. 工程根目录会生成 `.com-unity-codely.json`（本机端口，已忽略，不要提交）。
3. 外部 MCP 客户端连接到 `unity` 后即可使用编辑器侧能力。

## 目录说明

```
Assets/Scenes/HK132.unity    主场景
Assets/Scripts/              运行时脚本
Assets/Shaders/              扫描体与地面着色器
Assets/StreamingAssets/      字段映射与面板记忆默认值
.codely.packages/            Codely Bridge 本地包
Packages/manifest.json       包引用
```

## 版本

见 [Releases](https://github.com/deboerda/unity-cesium-digital-twin/releases)。1.2 起包含三维地面、多传感器 `detectors`、视角记忆，以及仓库内的 Codely Bridge。
