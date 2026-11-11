"""Regenerate the shipped compatibility inventory from DB179 and compiled admission tables."""
import argparse
import csv
import json
import re
from collections import Counter
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("database", type=Path, help="MouseDock devices.json (read only)")
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
native = root / "Native/PeripheralProbe"
database = json.loads(args.database.read_text(encoding="utf-8-sig"))
if database["database_version"] != 179:
    raise SystemExit("Review admission mappings before using another database version.")

admitted = {}
for module in ("ExtraKeyboard", "HeadsetController", "ExtraMouse"):
    for line in (native / (module + "Targets.inc")).read_text(encoding="utf-8-sig").splitlines():
        match = re.search(r'\{(0x[\da-f]+),\s*(0x[\da-f]+),[^\n]*?"([a-z0-9_]+)"', line, re.I)
        if match:
            vid, pid, protocol = match.groups()
            admitted.setdefault(protocol, {})[f"{int(vid,16):04X}:{int(pid,16):04X}"] = module

advanced = {
    "keycool_k19": "3151:5026", "valkyrie_vk99_ry": "374A:A263 374A:A278 374A:A281",
    "dareu_a98": "260D:0037 260D:0051", "dareu_flex87": "260D:0211",
    "mchose_k99v3": "3837:3033", "pmo_wave75": "36B0:3002", "ipi_aurora": "372E:106C",
    "titan_t87": "056D:C077", "rog_omni_keyboard": "0B05:1ACE 0B05:1AD0",
    "lofree": "388D:0025 388D:0024",
}
advanced_code = (native / "KeyboardAdvanced.cpp").read_text(encoding="utf-8-sig")
for protocol, identities in advanced.items():
    if f'"{protocol}"' not in advanced_code:
        raise SystemExit(f"Missing advanced adapter: {protocol}")
    admitted[protocol] = dict.fromkeys(identities.split(), "KeyboardAdvanced")

core = {}
for vid, pid, enum in re.findall(r'\{(0x[\da-f]+),\s*(0x[\da-f]+),\s*Protocol::(\w+)',
                                 (native / "Catalog.inc").read_text(), re.I):
    core.setdefault(enum, set()).add(f"{int(vid,16):04X}:{int(pid,16):04X}")
core_names = {
    "x23_standard": "X23", "x23_pro_input": "X23Passive",
    "aula_f87pro_input": "F87Passive", "blackshark_v3_passive": "Blackshark",
    "delux_pro_input": "DeluxPassive", "irok_royuan": "Irok", "langtu_t75": "Langtu",
    "pulsar_sonix": "Sonix", "rainy75_receiver": "Rainy75", "rainy75_wired": "Rainy75",
    "rog_delta2": "Pelta", "rog_pelta": "Pelta", "xiberia_t10": "Xiberia",
}
for protocol, enum in core_names.items():
    admitted[protocol] = dict.fromkeys(core.get(enum, ()), "Protocol::" + enum)
admitted["x23_freq"] = {identity: adapter for protocol, identities in admitted.items()
                         if protocol.startswith("x23_freq_") for identity, adapter in identities.items()}
admitted["valkyrie_m5"].update(admitted["royuan_mouse"])
admitted["atk_n9pro_serial"] = dict.fromkeys(("373B:11CB", "373B:1204"), "Special::N9Serial")
admitted["sony_h9_serial"] = {"054C:0E53": "Special::SonyH9"}
admitted["misiom"] = dict.fromkeys(("39A5:3D21", "3A95:3EE3", "39A5:3FF1", "3206:7015"), "Special::OHub")

limits = {
    "x23_freq": "共享VID/PID还须核对完整USB产品名及明确的协议变体；未知型号保持仅识别。",
    "vgn_zhou": "仅已定义的64字节报告；已观察到的32/32/16布局保持仅识别。",
    "vgn_neon75_air": "须MI1、明确col01及匹配的Feature报告。",
    "mchose_qhw": "核对产品名及配对板号；G98与K87 V3板号不可互换。",
    "rog_omni_keyboard": "须同一物理接收器的report1/report2配对接口及子键盘身份。",
    "aula_f87pro_input": "仅被动读取已定义报告；共享FA09身份须通过报告确认。",
    "delux_pro_input": "仅被动读取；共享FA60/FA65接口不发送主动探测。",
    "blackshark_v3_passive": "仅被动读取已定义报告。",
    "misiom": "须用户启用厂商扩展，且对应厂商驱动已安装；不安装或启动厂商软件。",
    "sony_h5": "须启用随包提供的HeadsetControl 4.1.0；默认关闭。",
}
common = "同时匹配接口、Usage、报告格式及适用的子设备身份；协议已实现，待实机验证。"
hardware_evidence = {
    ("blackshark_v2_hs", "1532:0565"): "2026-11-10/11 BlackShark V2 HS 2.4：MI3 FF14:1，实读76%→74%；0020明确区分耳机关机/开机，接收器保留；充电状态未知。",
    ("rog_azoth_x", "0B05:1A85"): "2026-11-10 ROG AZOTH接收器：实际100%、正在充电；稳定USB父设备序列号通过重复发现验证。",
}
rows = []
for section in ("wireless_receivers", "wireless_usb"):
    for item in database[section]:
        identity = f'{int(item["vendor"],16):04X}:{int(item["product"],16):04X}'
        protocol = item["protocol"]
        adapter = admitted.get(protocol, {}).get(identity)
        extension = protocol == "sony_h5" and identity == "054C:0EBF"
        status = "extension" if extension else "implemented" if adapter else "identityOnly"
        rows.append(dict(section=section, sourceProtocol=protocol, identity=identity,
                         name=item.get("name", ""), nativeAdapter=adapter or ("HeadsetControl 4.1.0" if extension else ""),
                         status=status, hardwareValidated=(protocol, identity) in hardware_evidence,
                         hardwareEvidence=hardware_evidence.get((protocol, identity), ""),
                         limit=limits.get(protocol, common if adapter else "仅识别；无匹配的已实现协议。")))

families = []
for protocol in sorted({row["sourceProtocol"] for row in rows}):
    group = [row for row in rows if row["sourceProtocol"] == protocol]
    statuses = {row["status"] for row in group}
    families.append(dict(sourceProtocol=protocol,
                         status=next(iter(statuses)) if len(statuses) == 1 else "partial",
                         sourceIdentities=sorted({row["identity"] for row in group}),
                         admittedIdentities=sorted({row["identity"] for row in group if row["status"] != "identityOnly"}),
                         nativeAdapters=sorted({row["nativeAdapter"] for row in group if row["nativeAdapter"]}),
                         hardwareValidated=all(row["hardwareValidated"] for row in group),
                         hardwareValidatedIdentities=sorted({row["identity"] for row in group if row["hardwareValidated"]}),
                         limit=limits.get(protocol, common)))
output = dict(schemaVersion=2, sourceDatabaseVersion=179, hardwareValidated=False,
              sourceReceiverRecords=len(database["wireless_receivers"]),
              sourceReceiverProtocols=len({item["protocol"] for item in database["wireless_receivers"]}),
              receiverStatusCounts=dict(Counter(row["status"] for row in rows if row["section"] == "wireless_receivers")),
              families=families, devices=rows,
              hardwareChecks=[
                  dict(name="AirPods Pro 2 USB-C", identity="Bluetooth 004C:2024", source="apple-ble-proximity", hardwareValidated=True,
                       evidence="2026-11-11：已配对经典蓝牙身份及型号匹配，单一邻近广播候选；左右耳约80%，盒未知。免驱估算按10%档位，不能验证广播归属；双耳非对称、充电及其他型号尚未实机验证。"),
                  dict(name="ROG AZOTH wired + receiver", identity="0B05:1A83 / 0B05:1A85", source="rog_azoth_x", hardwareValidated=True,
                       evidence="2026-11-10：用户切到有线模式并保留接收器；唯一USB父序列号相同，仅呈现一把键盘，保留相同稳定ID；有线100%、正在充电。"),
                  dict(name="Logitech G502 X", identity="046D:C547", source="hidpp20", hardwareValidated=True,
                       evidence="2026-11-10：HID++4.2、1004电量特性，实际27%、未充电；首个唤醒探测等待2500ms。"),
                  dict(name="Xiaomi 耳夹式耳机", source="windows-pnp", hardwareValidated=True,
                       evidence="2026-11-10：连接后实际100→97→96→94→89%，不是固定值或未知转零。"),
                  dict(name="FlyDigi BS2", source="bluetooth-bas", hardwareValidated=True,
                       evidence="2026-11-10：标准180F/2A19首次读取94%，通知订阅成功。"),
              ],
              additionalProviders=[
                  dict(name="AirPods proximity advertisement (driver-free estimate)", status="implemented",
                       limit="型号白名单及已连接的配对设备；8秒被动窗口、至少两包，多个同型号候选暂停更新。约10%档位；随机地址按邻近和型号关联，存在误归属限制，可在设置关闭。"),
                  dict(name="Windows properties / BLE BAS / declared standard HID / Windows gamepad battery",
                       status="implemented", limit="仅系统或协议明确的电量字段；档位、估算和多电池分量分别保留。"),
                  dict(name="HID++ / COMPX / Nordic / Razer / ROG / M HUB / Sonix", status="implemented",
                       limit="有限的已定义接口；HID++配对单元使用协议身份，不使用接收器槽位作为稳定ID。"),
                  dict(name="Beyerdynamic MMX 230 GAIA RFCOMM", status="identityOnly",
                       limit="现有资料无法证明不可伪装的型号/服务身份；仅显示识别信息，不发送RFCOMM请求。"),
              ])
destination = root / "docs"
(destination / "peripheral-compatibility.json").write_text(json.dumps(output, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
with (destination / "peripheral-compatibility.csv").open("w", encoding="utf-8-sig", newline="") as stream:
    writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
    writer.writeheader()
    writer.writerows(rows)
print(json.dumps(output["receiverStatusCounts"]))
for row in rows:
    if row["status"] == "identityOnly":
        print(row["sourceProtocol"], row["identity"])
