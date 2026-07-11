# SVSAP Machine Energy (SVSAPME)

版本：Ver1.4.1-rc1.0（内部版本：1.4.1-rc1.0）
适配：Stardew Valley 1.6.15 / SMAPI 4.5.2+
依赖：SVSAP 1.4.1-rc1.0+
源码：https://github.com/yuzeis/SVSAP-Machine-and-Energy-Project
Nexus：https://www.nexusmods.com/stardewvalley/mods/48640
3DM Mod：https://mod.3dmgame.com/

SVSAPME 是 SVSAP 的电力扩展，加入发电机、太阳能板、电池/储能、耗电机器、Powered Importer/Exporter/Interface、电力炉子、电力碎晶机、单方块农场，以及独立单方块酒桶/陈酿桶机器族。

## 下载与安装

发布包由 ModBuildConfig 在 Release 编译时自动生成，文件名为 `SVSAPME 1.4.1-rc1.0.zip`。本次为 Ver1.4.1-rc1.0 发布候选版本，并非最终正式版。

安装方法：

1. 先安装 Stardew Valley 1.6.15、SMAPI 4.5.2 或更新版本。
2. 安装 `SVSAP 1.4.1-rc1.0`。
3. 解压 `SVSAPME 1.4.1-rc1.0.zip`。
4. 将解压出的 `SVSAPME` 文件夹放入 `Stardew Valley/Mods/`。
5. 通过 SMAPI 启动游戏。

## 多人联机

主机与所有客机必须安装相同版本的 SVSAP 和 SVSAPME。能源网络与机器状态由主机保存，客机操作会通过多人消息交由主机执行。单方块农场、酒桶和陈酿桶也由 SVSAPME 主机状态保存；更新 Ver1.4.1-rc1.0 时，请让服务器与所有 farmhand 同步替换两个 mod。

## 配置

首次启动后 SMAPI 会自动生成 `SVSAPME/config.json`。发布包默认不包含 `config.json`，避免覆盖玩家本地配置。

## 控制台命令

- `svsapme_ids`：列出 SVSAPME 物品 ID。
- `svsapme_balance`：输出配方与平衡表。
- `svsapme_debug_network`：查看能源网络调试信息。
- `svsapme_claim`：领取异常回收箱。
- `svsapme_energy_report <networkGuid>`：查看指定 SVSAP 网络的储能/容量。
- `svsapme_selftest`：Debug 构建限定，运行完整运行时自测；Release 包不包含此命令。

## 注意

SVSAPME 使用模组 ID `Koizumi.SVSAPME`。公开游玩前建议备份存档。
