// engine/backup/backup.py's load (SB-P7-10) with the database's tools stood in: its own Python, its own code, every
// psql, createdb, pg_restore and dropdb call recorded instead of run, and a throwaway folder for the engine's data. A
// manifest naming a file by a path no backup writes ('..' among its parts) loads nothing and writes nothing outside the
// archive; an archive file whose copy is cut short (a full disk) never takes its name, so the retry loads; and a load
// stopped part way (Ctrl+C) drops the database it made and says so. cli/chaos.sh runs the real load on a real stack.
// Needs Python 3.11 or later (the backup image's); skipped, saying so, without it.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

const backup = fileURLToPath(new URL("../engine/backup/backup.py", import.meta.url));
const python = ["python3", "python"].find((p) =>
  spawnSync(p, ["-c", "import sys; sys.exit(0 if sys.version_info >= (3, 11) else 1)"], { encoding: "utf8", timeout: 20000 }).status === 0);
const skip = python ? false : "NOT RUN: needs Python 3.11 or later (python3 or python), as the backup image has";

// The harness: backup.py loaded from its own path, `run` recording each call (and failing the one the case names), the
// owner change a no-op (the folders are this user's), then the case. Every check is an assert; exit 0 is a pass.
const harness = String.raw`
import hashlib, importlib.util, json, os, re, shutil, sys
from pathlib import Path
spec = importlib.util.spec_from_file_location("backup", sys.argv[1])
b = importlib.util.module_from_spec(spec); spec.loader.exec_module(b)
case, work = sys.argv[2], Path(sys.argv[3])
calls = []
def run(args, env=None):
    args = [str(a) for a in args]
    calls.append(args)
    tool = Path(args[0]).name
    if tool == "pg_restore" and case in ("stopped", "uncertain"):
        raise KeyboardInterrupt()
    if "--tuples-only" in args:
        if case == "uncertain":
            raise RuntimeError("database status unavailable")
        return "\n".join(sorted(re.findall(r"'([a-z_0-9]+)'", args[-1])[::2])) + "\n"
    return ""
b.run = run
os.chown = lambda *a, **k: None
live = work / "live"
archive = live / "archive"
(archive / "ab").mkdir(parents=True)
(live / "state").mkdir()
os.environ.update({"RHYTHM_ARCHIVE": str(archive), "RHYTHM_ARCHIVE_OWNER": "0:0", "PGDATABASE": "rhythm",
                   "RHYTHM_REFRESH_STATE": str(live / "state" / "graph-refresh.state")})

def folder(files, listed=None):
    f = work / ("restored-" + str(len(list(work.iterdir()))))
    for name, text in files.items():
        (f / name).parent.mkdir(parents=True, exist_ok=True)
        (f / name).write_text(text)
    names = listed if listed is not None else list(files)
    manifest = {"format": 1, "files": {n: hashlib.sha256((f / n).resolve().read_bytes()).hexdigest() for n in names}}
    (f / "manifest.json").write_text(json.dumps(manifest))
    return f

def refused(f, says):
    try:
        b.load(f)
    except b.RestoreRefused as e:
        assert says in str(e), str(e)
        return str(e)
    raise AssertionError("loaded")

tools = lambda: [Path(c[0]).name for c in calls]
if case == "outside":
    f = folder({"database.dump": "DEMO dump", "archive/ab/one.txt": "DEMO one", "state/evil": "DEMO planted"},
               ["database.dump", "archive/ab/one.txt", "state/evil", "archive/ab/../../state/evil"])
    refused(f, "a path no backup writes (archive/ab/../../state/evil); nothing was loaded")
    assert not (live / "state" / "evil").exists(), "written outside the archive"
    assert calls == [], calls
    # load's own check, past the manifest's: a name that would land outside the archive is refused before anything.
    b.contents, contents = (lambda folder: [Path("ab/../../state/evil")]), b.contents
    refused(f, "would land outside the engine's archive; nothing was loaded")
    assert not (live / "state" / "evil").exists() and calls == []
    b.contents = contents
    # An empty or '.' part names a file the folder has, so only this check refuses it; an absolute name fails the
    # integrity check first on either system.
    for bad, same, says in [("archive//ab/one.txt", "archive/ab/one.txt", "no backup writes"),
                            ("./database.dump", "database.dump", "no backup writes"), ("/database.dump", "database.dump", "")]:
        g = folder({"database.dump": "DEMO dump", "archive/ab/one.txt": "DEMO one"})
        m = json.loads((g / "manifest.json").read_text()); m["files"][bad] = m["files"][same]
        (g / "manifest.json").write_text(json.dumps(m))
        try: b.contents(g)
        except RuntimeError as e: assert says in str(e) and "nothing was loaded" in str(e) or not says and "integrity" in str(e), str(e)
        else: raise AssertionError(bad + " was taken")
elif case == "cut":
    f = folder({"database.dump": "DEMO dump", "archive/ab/one.txt": "DEMO one, whole", "archive/cd/two.txt": "DEMO two"})
    copy = shutil.copyfile
    def full(source, target, **k):
        Path(target).write_text("DEMO o")
        raise OSError(28, "No space left on device")
    shutil.copyfile = full
    said = refused(f, "the load failed; the live database was not switched; the half-loaded database rhythm_restore_")
    assert "dropdb" in tools(), tools()
    assert not (archive / "ab" / "one.txt").exists(), "a copy cut short took its name"
    assert list(archive.rglob("*.tmp")) == [], list(archive.rglob("*.tmp"))
    shutil.copyfile = copy
    calls.clear()
    b.load(f)
    assert (archive / "ab" / "one.txt").read_text() == "DEMO one, whole" and (archive / "cd" / "two.txt").read_text() == "DEMO two"
    assert list(archive.rglob("*.tmp")) == []
    assert "dropdb" not in tools() and any("RENAME TO" in " ".join(c) for c in calls), calls
    # Loaded again, every file is the backup's own: nothing to add, nothing refused.
    calls.clear()
    b.load(f)
elif case == "retention":
    os.environ["RHYTHM_BACKUP_SERIES"] = "test"
    os.environ.pop("RHYTHM_BACKUP_RETENTION", None)
    b.retention()
    assert not calls
    b.retention(preview=True)
    assert "--dry-run" in calls[-1] and "--prune" not in calls[-1]
    os.environ["RHYTHM_BACKUP_RETENTION"] = "on"
    b.retention()
    assert calls[-1][1:] == ["forget", "--tag", "rhythm:test", "--group-by", "tags",
                             "--keep-daily", "7", "--keep-weekly", "5", "--keep-monthly", "12", "--prune"]
elif case == "stopped":
    f = folder({"database.dump": "DEMO dump", "archive/ab/one.txt": "DEMO one"})
    refused(f, "the load was stopped; the live database was not switched; the half-loaded database rhythm_restore_")
    assert tools()[:2] == ["createdb", "pg_restore"] and "dropdb" in tools(), tools()
    assert not (archive / "ab" / "one.txt").exists()
elif case == "uncertain":
    f = folder({"database.dump": "DEMO dump", "archive/ab/one.txt": "DEMO one"})
    try:
        b.load(f)
    except RuntimeError as e:
        assert not isinstance(e, b.RestoreRefused) and "check which database" in str(e), str(e)
    else:
        raise AssertionError("unconfirmed database switch was accepted")
else:
    raise SystemExit("unknown case " + case)
`;

function load(t, which) {
  const dir = mkdtempSync(join(tmpdir(), "rhythm-load-"));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  const script = join(dir, "harness.py");
  writeFileSync(script, harness);
  const work = join(dir, "work");
  mkdirSync(work);
  const env = { ...process.env };
  for (const name of ["RHYTHM_ARCHIVE", "RHYTHM_ARCHIVE_OWNER", "RHYTHM_REFRESH_STATE", "PGDATABASE"]) delete env[name];
  // -B: no bytecode cache beside backup.py.
  const result = spawnSync(python, ["-I", "-B", script, backup, which, work], { encoding: "utf8", env, timeout: 60000 });
  assert.equal(result.status, 0, result.stdout + result.stderr);
}

test("a manifest naming a file by a path no backup writes loads nothing and writes nothing outside the archive", { skip }, (t) =>
  load(t, "outside"));
test("an archive file whose copy is cut short never takes its name, and the retry loads", { skip }, (t) => load(t, "cut"));
test("a load stopped part way drops the database it made and says the live one was not switched", { skip }, (t) => load(t, "stopped"));
test("an unavailable database after an interrupted restore cannot authorize a restart", { skip }, (t) => load(t, "uncertain"));
test("retention needs opt-in and selects only this project; preview never prunes", { skip }, (t) => load(t, "retention"));
