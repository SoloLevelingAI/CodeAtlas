"""Project the LikeC4 model into docs/architecture/target-model.json.

The .c4 sources are authoritative. This script runs `likec4 export json` and writes
the stable document CodeAtlas reads. --check is read-only. --diagrams also exports
PNG views into the Web host.
"""
from pathlib import Path
import argparse
import json
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1]
project = root / "docs/architecture"
output = project / "target-model.json"
diagrams = root / "src/CodeAtlas.Host/wwwroot/architecture"
view_levels = {
    "context": "system",
    "container": "host",
    "component": "module",
    "host-desktop": "host",
    "host-web": "host",
    "host-cli": "host",
}

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--check", action="store_true")
parser.add_argument("--diagrams", action="store_true")
args = parser.parse_args()


def likec4(*arguments):
    binary = project / "node_modules/.bin/likec4"
    command = [str(binary)] if binary.exists() else ["npx", "--yes", "likec4@1.53.0"]
    completed = subprocess.run([*command, *arguments, str(project)], cwd=root, text=True, capture_output=True)
    if completed.returncode != 0:
        raise SystemExit(completed.stderr or completed.stdout or "likec4 failed")
    return completed


def as_list(value):
    if value is None:
        return []
    return value if isinstance(value, list) else [value]


def split_capability(value, required):
    parts = value.split("|")
    if len(parts) < 3:
        raise ValueError("能力必须是 id|契约|版本：" + value)
    return {
        "id": parts[0],
        "contract": parts[1],
        "version": parts[2],
        **({"optional": parts[3] == "true"} if required else {}),
    }


def project_model(exported):
    computed = next(item for item in exported if item.get("projectId") == "codeatlas")
    elements = computed["elements"]
    system = next(item for item in elements.values() if item["kind"] == "system")

    def atlas_id(element):
        value = element.get("metadata", {}).get("atlasId")
        if not value:
            raise ValueError("元素缺少 atlasId：" + element["id"])
        return value

    def parent_id(element_id):
        if "." not in element_id:
            return None
        parent = elements.get(element_id.rsplit(".", 1)[0])
        if parent is None or parent["kind"] != "component":
            return None
        return atlas_id(parent)

    modules = []
    for element in elements.values():
        if element["kind"] != "component":
            continue
        meta = element.get("metadata", {})
        modules.append({
            "id": atlas_id(element),
            "name": element["title"],
            "owner": meta.get("owner", ""),
            "domain": meta.get("domain", ""),
            "parent": parent_id(element["id"]),
            "runtime": meta.get("runtime") == "true",
            "provides": [split_capability(item, False) for item in as_list(meta.get("provides"))],
            "requires": [split_capability(item, True) for item in as_list(meta.get("requires"))],
            "dependencies": [],
            "implementations": as_list(meta.get("implementations")),
        })
    by_fqn = {element["id"]: atlas_id(element) for element in elements.values() if element["kind"] == "component"}
    dependencies = {module["id"]: [] for module in modules}
    host_modules = {element["id"]: [] for element in elements.values() if element["kind"] == "container"}
    for relation in computed["relations"].values():
        source = relation["source"]["model"]
        target = relation["target"]["model"]
        if relation["kind"] == "dependency" and source in by_fqn and target in by_fqn:
            dependencies[by_fqn[source]].append(by_fqn[target])
        if relation["kind"] == "uses" and source in host_modules and target in by_fqn:
            host_modules[source].append(by_fqn[target])
    for module in modules:
        module["dependencies"] = sorted(set(dependencies[module["id"]]))
    hosts = []
    for element in elements.values():
        if element["kind"] != "container":
            continue
        meta = element.get("metadata", {})
        hosts.append({
            "id": atlas_id(element),
            "name": element["title"],
            "technology": element.get("technology") or "",
            "owner": meta.get("owner", ""),
            "domain": meta.get("domain", ""),
            "modules": sorted(set(host_modules[element["id"]])),
        })
    views = []
    for view_id, level in view_levels.items():
        view = computed["views"].get(view_id)
        if view is None:
            raise ValueError("缺少视图：" + view_id)
        views.append({
            "id": view_id,
            "title": view.get("title") or view_id,
            "level": level,
            "diagram": f"architecture/{view_id}.png",
        })
    system_meta = system.get("metadata", {})
    return {
        "schema": 1,
        "format": "likec4",
        "system": {
            "id": atlas_id(system),
            "name": system["title"],
            "owner": system_meta.get("owner", ""),
            "domain": system_meta.get("domain", ""),
        },
        "hosts": sorted(hosts, key=lambda item: item["id"]),
        "modules": sorted(modules, key=lambda item: item["id"]),
        "views": views,
    }


def canonical(data):
    return json.dumps(data, ensure_ascii=False, indent=2) + "\n"


def export_model():
    with tempfile.TemporaryDirectory() as directory:
        target = Path(directory) / "likec4.json"
        likec4("export", "json", "-o", str(target), "--pretty", "--skip-layout")
        return json.loads(target.read_text(encoding="utf-8"))


def export_diagrams():
    diagrams.mkdir(parents=True, exist_ok=True)
    likec4("export", "png", "-o", str(diagrams), "--flat")


model = project_model(export_model())
text = canonical(model)
if args.diagrams:
    export_diagrams()
    missing = [view["diagram"] for view in model["views"]
               if not (root / "src/CodeAtlas.Host/wwwroot" / view["diagram"]).is_file()]
    if missing:
        raise SystemExit("缺少 LikeC4 图：" + ", ".join(missing))
if args.check:
    current = output.read_text(encoding="utf-8") if output.exists() else ""
    if current != text:
        raise SystemExit("LikeC4 模型与 target-model.json 不一致。请运行 python scripts/sync-likec4.py。")
    print("PASS: LikeC4 模型与 target-model.json 一致。")
else:
    output.write_text(text, encoding="utf-8")
    print("Generated docs/architecture/target-model.json from LikeC4.")
