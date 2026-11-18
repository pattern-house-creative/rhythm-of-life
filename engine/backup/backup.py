"""Encrypted restic backup of a consistent PostgreSQL dump and immutable archive.

Needs PGHOST, PGPORT, PGUSER, PGDATABASE, PGPASSFILE; RESTIC_REPOSITORY,
RESTIC_PASSWORD_FILE; PG_DUMP and RESTIC may name explicit executable paths.
Run on encrypted storage. Never pass a password or connection URL as an argument.
Only the scheduled retention command can remove tagged snapshots. Restore never restarts a service;
load replaces the live database only by renaming, and keeps the one it replaces.
The one thing it drops is a database a failed load made itself and never switched to.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import sys
import time


def run(args, env=None):
    result = subprocess.run([str(a) for a in args], capture_output=True, text=True, env=env)
    if result.returncode:
        raise RuntimeError(f"{Path(str(args[0])).name} failed with exit {result.returncode}; output suppressed to protect source data")
    return result.stdout


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verify(folder):
    folder = Path(folder).resolve()
    manifest = json.loads((folder / "manifest.json").read_text())
    for name, expected in manifest["files"].items():
        path = (folder / name).resolve()
        if not path.is_relative_to(folder) or not path.is_file() or digest(path) != expected:
            raise RuntimeError("Backup integrity check failed")
    return len(manifest["files"])


def series_tag():
    return "rhythm:" + os.environ.get("RHYTHM_BACKUP_SERIES", "rhythm")


def retention(preview=False):
    mode = os.environ.get("RHYTHM_BACKUP_RETENTION", "off")
    if mode not in ("off", "on"):
        raise ValueError("RHYTHM_BACKUP_RETENTION must be off or on")
    if not preview and mode == "off":
        return
    command = [os.environ.get("RESTIC", "restic"), "forget", "--tag", series_tag(), "--group-by", "tags",
               "--keep-daily", "7", "--keep-weekly", "5", "--keep-monthly", "12"]
    if preview:
        command.append("--dry-run")
    else:
        command.append("--prune")
    # The preview contains snapshot IDs and staging paths; only print it on explicit request.
    output = run(command)
    if preview:
        print(output, end="")


def status(archive):
    repository = os.environ["RESTIC_REPOSITORY"]
    if ":" not in repository:
        repo = Path(repository)
        if not (repo / "config").is_file():
            print("  note  No encrypted backup repository found here yet.")
            return
        if repo.stat().st_dev == Path(archive).stat().st_dev:
            print("  note  The backup repository and engine data are on the same filesystem; keep a separate copy.")
    snapshots = json.loads(run([os.environ.get("RESTIC", "restic"), "--no-lock", "snapshots", "--json"])) or []
    ours = [row for row in snapshots if series_tag() in (row.get("tags") or [])]
    legacy = False
    if not ours:
        ours = [row for row in snapshots if not row.get("tags")]
        legacy = bool(ours)
    if not ours:
        print("  note  No snapshot found for this install.")
        return
    latest = max(datetime.fromisoformat(row["time"].replace("Z", "+00:00")) for row in ours)
    hours = (datetime.now(timezone.utc) - latest).total_seconds() / 3600
    label = "Latest untagged snapshot in this repository (install unverified)" if legacy else "Latest backup snapshot"
    state = "note" if legacy or hours >= 48 or hours < 0 else "ok  "
    warning = "; older than the 48-hour limit" if hours >= 48 else "; snapshot clock is ahead" if hours < 0 else ""
    print(f"  {state}  {label}: {latest.isoformat()}, {hours:.1f} hours ago{warning}.")


def backup(archive, staging):
    archive, staging = Path(archive).resolve(), Path(staging).resolve()
    if staging == archive or staging.is_relative_to(archive) or archive.is_relative_to(staging):
        raise ValueError("Archive and new staging directory must be disjoint")
    if not os.environ.get("RESTIC_PASSWORD_FILE") or not os.environ.get("RESTIC_REPOSITORY"):
        raise ValueError("Encrypted restic repository and password file required")
    repository = os.environ["RESTIC_REPOSITORY"]
    # The first backup into a local folder creates the repository (a remote one is created by its owner).
    if ":" not in repository and not (Path(repository) / "config").exists():
        run([os.environ.get("RESTIC", "restic"), "init"])
    staging.mkdir(parents=True, exist_ok=False)
    # The DB snapshot references only files already atomically published. Archive files are immutable
    # and this platform never garbage-collects them, so copying after pg_dump is consistent.
    run([os.environ.get("PG_DUMP", "pg_dump"), "--format=custom", "--no-owner", "--no-acl", "--file", staging / "database.dump"])
    if archive.exists():
        shutil.copytree(archive, staging / "archive", ignore=shutil.ignore_patterns("*.tmp"))
    else:  # nothing archived yet
        (staging / "archive").mkdir()
    originals_dir = os.environ.get("RHYTHM_BACKUP_ORIGINALS_DIR")
    if originals_dir:
        originals = Path(originals_dir).resolve(strict=True)
        if originals.is_relative_to(staging) or staging.is_relative_to(originals):
            raise ValueError("Originals and staging directories must be disjoint")
        # Provider originals are recovered separately from the engine archive. Reject links instead of
        # following a link out of the approved source folder when copying or hashing its contents.
        copied = staging / "originals"
        shutil.copytree(originals, copied, symlinks=True)
        if any(path.is_symlink() for path in copied.rglob("*")):
            raise ValueError("The originals folder contains a symbolic link")
        if not any(path.is_file() for path in copied.rglob("*")):
            raise ValueError("The required originals folder contains no files")
    manifest = {"format": 1, "files": {p.relative_to(staging).as_posix(): digest(p)
                for p in staging.rglob("*") if p.is_file()}}
    (staging / "manifest.json").write_text(json.dumps(manifest, sort_keys=True), encoding="utf-8")
    verify(staging)
    # Save relative paths so recovery is portable between Windows and Linux.
    result = subprocess.run([os.environ.get("RESTIC", "restic"), "backup", "--json", "--tag", series_tag(), "."],
                            cwd=staging, capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError("Encrypted backup failed; staging retained for recovery")
    summary = [json.loads(line) for line in result.stdout.splitlines() if line.startswith("{")]
    snapshot = next((x["snapshot_id"] for x in summary if x.get("message_type") == "summary"), None)
    if not snapshot:
        raise RuntimeError("Backup did not return a snapshot ID")
    run([os.environ.get("RESTIC", "restic"), "check", "--read-data"])
    print(json.dumps({"snapshot": snapshot, "verified_files": len(manifest["files"])}))


def restore(snapshot, target):
    target = Path(target).resolve()
    if target.exists():
        raise ValueError("Restore destination must be new")
    run([os.environ.get("RESTIC", "restic"), "restore", snapshot, "--target", target])
    count = verify(target)
    print(json.dumps({"verified_files": count, "database_restored": False,
                      "next": "load <folder> makes it the install's data (rhythm restore runs it next); keep the engine and any exposure stopped until then."}))


def contents(folder):
    """The restored folder's files as its manifest lists them, checked against it: refused when the folder holds a
    link, or a file the manifest does not list, since a restore writes exactly a snapshot's files and anything else
    was put there afterwards, or when the manifest names a file by a path a backup never writes (absolute, or with an
    empty, '.' or '..' part), which could name a place outside the folder it is loaded into (SB-P7-10). Returns the
    archive files' paths relative to folder/archive."""
    folder = Path(folder).resolve()
    verify(folder)
    listed = set(json.loads((folder / "manifest.json").read_text())["files"])
    for name in sorted(listed):
        if name.startswith("/") or any(part in ("", ".", "..") for part in name.split("/")):
            raise RuntimeError(f"The restored folder's manifest names a file by a path no backup writes ({name}); "
                               "nothing was loaded")
    for path in sorted(folder.rglob("*")):
        name = path.relative_to(folder).as_posix()
        if path.is_symlink():
            raise RuntimeError(f"The restored folder holds a link ({name}); nothing was loaded")
        if path.is_file() and name != "manifest.json" and name not in listed:
            raise RuntimeError(f"The restored folder holds a file its manifest does not list ({name}); nothing was loaded")
    if "database.dump" not in listed:
        raise RuntimeError("The restored folder has no database.dump; nothing was loaded")
    return sorted(Path(name).relative_to("archive") for name in listed if name.startswith("archive/"))


class RestoreRefused(RuntimeError):
    """The load did not switch databases. The wrappers can resume previously running services."""


def load(folder):
    """Makes a restored folder the install's data: its database.dump into a NEW database, quarantined
    (restore-quarantine.sql), its archive files added to the engine's archive (never replacing one), then the new
    database takes the live one's name and the live one is kept as rhythm_before_<time>. Run with the engine and
    the app stopped; the next migrate brings the schema and the engine's login up to date, and the engine rebuilds
    the map (the quarantine revoked its rows) as soon as it starts.

    Everything that can refuse is checked before anything changes: the folder against its manifest (contents), each
    archive file's place inside the engine's archive, and each archive file the engine already has against the
    backup's copy. A step that fails or is stopped after the new database is made (pg_restore, the quarantine, adding
    the archive's files, the rename) drops that new database, which this load made and nothing ever used, so a retry
    starts clean and no unquarantined copy is left beside the live one; the live database is untouched until the
    rename, which is the last step. An archive file is copied beside its place and only then given its name, never
    over a file there, so one cut short leaves no file a retry would find differing (SB-P7-10)."""
    folder = Path(folder).resolve()
    try:
        files = contents(folder)
    except RuntimeError as error:
        raise RestoreRefused(str(error)) from error
    archive = Path(os.environ.get("RHYTHM_ARCHIVE", "/var/lib/rhythm/archive"))
    owner = os.environ.get("RHYTHM_ARCHIVE_OWNER") or f"{os.getuid()}:{os.getgid()}"
    uid, gid = (int(x) for x in owner.split(":"))
    adding = []
    for name in files:
        source, target = folder / "archive" / name, archive / name
        if not target.resolve().is_relative_to(archive.resolve()):
            raise RestoreRefused("An archive file would land outside the engine's archive; nothing was loaded")
        if target.exists():
            if not target.is_file() or digest(target) != digest(source):
                raise RestoreRefused("An archive file differs from the backup's copy; nothing was loaded")
            continue
        adding.append((source, target))
    live = os.environ.get("PGDATABASE", "rhythm")
    stamp = time.strftime("%Y%m%d%H%M%S", time.gmtime())
    new, kept = f"{live}_restore_{stamp}", f"{live}_before_{stamp}"
    maintenance = {**os.environ, "PGDATABASE": "postgres"}
    psql = os.environ.get("PSQL", "psql")
    run([os.environ.get("CREATEDB", "createdb"), new], env=maintenance)
    added = 0
    try:
        run([os.environ.get("PG_RESTORE", "pg_restore"), "--no-owner", "--no-acl", "--exit-on-error", "--dbname", new, folder / "database.dump"])
        run([psql, "-v", "ON_ERROR_STOP=1", "--dbname", new, "--file", Path(__file__).with_name("restore-quarantine.sql")])
        for source, target in adding:
            for parent in reversed([d for d in target.parents if d.is_relative_to(archive) and not d.exists()]):
                parent.mkdir()
                os.chmod(parent, 0o755)
                os.chown(parent, uid, gid)
            # Beside its place under a name of its own (backups leave out *.tmp), then linked to its name, which fails
            # if a file is there, so a copy cut short (a full disk) never takes the name.
            part = target.with_name(f"{target.name}.{secrets.token_hex(8)}.tmp")
            try:
                shutil.copyfile(source, part)
                os.chmod(part, 0o644)
                os.chown(part, uid, gid)
                os.link(part, target)
            finally:
                part.unlink(missing_ok=True)
            added += 1
        run([psql, "-v", "ON_ERROR_STOP=1", "-c",
             f"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname IN ('{live}','{new}') AND pid<>pg_backend_pid()",
             "-c", f'ALTER DATABASE "{live}" RENAME TO "{kept}"', "-c", f'ALTER DATABASE "{new}" RENAME TO "{live}"'], env=maintenance)
    except BaseException as failed:
        # Stopped too (Ctrl+C): the half-loaded database is dropped all the same, and the load fails saying so.
        reason = (str(failed) if isinstance(failed, (ValueError, RuntimeError))
                  else "the load was stopped" if isinstance(failed, (KeyboardInterrupt, SystemExit)) else "the load failed")
        files_note = f"; {added} archive file(s) it added stay, as archive files are never replaced" if added else ""
        # The rename is one statement each way: while the live database still has its name, nothing switched, and
        # the new one, which only this load ever used, goes.
        try:
            names = run([psql, "-v", "ON_ERROR_STOP=1", "--tuples-only", "--no-align", "-c",
                         f"SELECT datname FROM pg_database WHERE datname IN ('{live}','{kept}','{new}') ORDER BY 1"], env=maintenance).split()
        except RuntimeError:
            raise RuntimeError(f"{reason}; the server could not be asked what was switched: check which database is {live} "
                               f"before starting the stack, and drop {new} if it is not{files_note}") from failed
        if live in names and new in names:
            try:
                run([os.environ.get("DROPDB", "dropdb"), new], env=maintenance)
                left = f"; the half-loaded database {new} was dropped"
            except RuntimeError:
                left = f"; drop the half-loaded database {new} yourself"
            raise RestoreRefused(f"{reason}; the live database was not switched{left}{files_note}") from failed
        raise RuntimeError(f"{reason}; the switch stopped half way: rename {kept} back to {live} (or {new} to {live}) "
                           f"before starting the stack{files_note}") from failed
    # The engine's last-rebuild time would make it wait up to graph.refreshSeconds before rebuilding the revoked map.
    Path(os.environ.get("RHYTHM_REFRESH_STATE", archive.parent / "state" / "graph-refresh.state")).unlink(missing_ok=True)
    print(json.dumps({"database_restored": True, "archive_files_added": added, "previous_database": kept,
                      "next": "Start the stack (rhythm up); every token was disabled, so create new ones (rhythm token create)."}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="action", required=True)
    b = sub.add_parser("backup"); b.add_argument("archive"); b.add_argument("staging")
    r = sub.add_parser("restore"); r.add_argument("snapshot"); r.add_argument("target")
    v = sub.add_parser("verify"); v.add_argument("target")
    l = sub.add_parser("load"); l.add_argument("folder")
    sub.add_parser("status")
    policy = sub.add_parser("retention"); policy.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    try:
        if args.action == "backup": backup(args.archive, args.staging)
        elif args.action == "status": status("/var/lib/rhythm")
        elif args.action == "retention": retention(args.dry_run)
        elif args.action == "restore": restore(args.snapshot, args.target)
        elif args.action == "load": load(args.folder)
        else: print(json.dumps({"verified_files": verify(args.target)}))
    except RestoreRefused as exc:
        print(str(exc), file=sys.stderr)
        sys.exit(12)
    except Exception as exc:
        print(str(exc) if isinstance(exc, (ValueError, RuntimeError)) else "Backup operation failed; private details suppressed", file=sys.stderr)
        sys.exit(1)
