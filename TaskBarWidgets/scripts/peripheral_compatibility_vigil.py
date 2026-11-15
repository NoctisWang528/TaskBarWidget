"""Add the reviewed VigilWatt integration to the existing compatibility inventory."""
from pathlib import Path
import re

PINNED = "4df830043296760f5d5c273f4cdc0ea9456c4bc7"


def append_vigil(output, root):
    rows = [row for row in output["devices"] if row["sourceProtocol"] != "vigil-razer-90b"]
    for identity, name in [("1532:00E2", "Razer Basilisk V4 Pro"),
                           ("1532:00E3", "Razer Basilisk V4 Pro receiver"),
                           ("1532:00E5", "Razer Viper V4 Pro"),
                           ("1532:00E6", "Razer Viper V4 Pro receiver")]:
        rows.append(dict(section="vigil_watt", sourceProtocol="vigil-razer-90b", identity=identity,
                         name=name, nativeAdapter="PeripheralHost", status="implemented",
                         hardwareValidated=False, hardwareEvidence="",
                         limit="91-byte Feature channel; strict response validation; charging may be unknown. Protocol implemented, pending TaskBarWidgets hardware verification."))
    output["devices"] = rows
    registry = (root / "PeripheralHost/PluginRegistry.cs").read_text(encoding="utf-8-sig")
    output["vigilWatt"] = dict(source="https://github.com/evren-oct/vigil-watt", commit=PINNED,
        license="MIT", synchronization="development-time pinned import; shipped with application",
        plugins=re.findall(r"new VigilWatt.Core.Plugins.([A-Za-z0-9_.]+)\(", registry),
        hardwareValidated=False,
        note="The registry lists integrated protocol plugins, not proof that every product or firmware is supported. Admission also requires matching interface capabilities and validated replies. Existing hardwareChecks describe earlier native-provider observations only.")
    return output
