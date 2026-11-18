#!/usr/bin/env python3
"""Run the SQL firewall steps of backend-deploy.yml against a stub az and curl.

backend-deploy.yml runs only after a merge to main and talks to Azure, so no PR
check runs its scripts. Since #504 the job lets its runner through the SQL
firewall for the migrations: "Open SQL firewall for this runner" adds a rule
for the runner's public IP, "Apply database migrations" uses it, and "Close
SQL firewall" removes it at the end, whatever happened in between. When the
deploy identity may not write firewall rules, the open step has to fail with
an error that names the missing permission.

This takes each step's `run:` script out of the workflow, runs it with bash
with stub `az` and `curl` commands first on PATH, and checks the commands it
issues, the step outputs it writes and the errors it prints. It also checks
how the steps are wired: their order, env and `if:`. Workflows CI runs it
(#504); run the same command from the repo root to check locally.

Usage: python3 .github/scripts/test-backend-deploy-firewall.py [REPO_ROOT]
REPO_ROOT defaults to the repo this script is in. YAML is read with PyYAML, or
with yq (preinstalled on GitHub's ubuntu runners) when PyYAML is missing.
Needs bash, sed, tr, grep and head on PATH (Git Bash's on Windows).
"""

import json
import os
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

WORKFLOW = ".github/workflows/backend-deploy.yml"
JOB = "build-and-deploy"
OPEN = "Open SQL firewall for this runner"
MIGRATE = "Apply database migrations"
DEPLOY = "Deploy to Azure App Service"
CLOSE = "Close SQL firewall"
LOGIN = "Azure login (OIDC)"

RG = "anchor-rg"
WEBAPP = "anchor-api-test"
SERVER = "anchor-sql-test"
RULE = "gh-backend-deploy-4242"
PASSWORD = "Pa55%word"
CONN = (
    f"Server=tcp:{SERVER}.database.windows.net,1433;Database=anchordb;"
    f"User ID=anchoradmin;Password={PASSWORD};Encrypt=true;"
    "TrustServerCertificate=false;"
)
DENIED = (
    "ERROR: (AuthorizationFailed) The client '11111111-2222-3333-4444-555555555555' "
    "with object id '11111111-2222-3333-4444-555555555555' does not have "
    "authorization to perform action 'Microsoft.Sql/servers/firewallRules/write' "
    f"over scope '/subscriptions/0000/resourceGroups/{RG}/providers/"
    f"Microsoft.Sql/servers/{SERVER}/firewallRules/{RULE}' or the scope is invalid.\n"
    "Code: AuthorizationFailed"
)

# Answers the calls the steps make from STUB_* variables and logs each one.
STUB_AZ = """#!/usr/bin/env bash
printf 'az %s\\n' "$*" >> "$STUB_LOG"
case "$*" in
  "resource list "*) printf '%s\\n' "${STUB_RG-}" ;;
  "webapp config connection-string list "*) printf '%s\\n' "${STUB_CONN-}" ;;
  "sql server firewall-rule create "*)
    if [ -n "${STUB_CREATE_ERROR-}" ]; then printf '%s\\n' "$STUB_CREATE_ERROR" >&2; exit 1; fi ;;
  "sql server firewall-rule delete "*)
    if [ -n "${STUB_DELETE_ERROR-}" ]; then printf '%s\\n' "$STUB_DELETE_ERROR" >&2; exit 1; fi ;;
  *) echo "stub az: unexpected call: $*" >&2; exit 2 ;;
esac
"""

# Answers with STUB_IPIFY / STUB_CHECKIP for the two IP services, else fails
# the way `curl -f` does on an HTTP error.
STUB_CURL = """#!/usr/bin/env bash
printf 'curl %s\\n' "$*" >> "$STUB_LOG"
url=${*: -1}
case "$url" in
  https://api.ipify.org) [ -n "${STUB_IPIFY-}" ] && { printf '%s' "$STUB_IPIFY"; exit 0; } ;;
  https://checkip.amazonaws.com) [ -n "${STUB_CHECKIP-}" ] && { printf '%s\\n' "$STUB_CHECKIP"; exit 0; } ;;
esac
echo "curl: (22) The requested URL returned error: 503" >&2
exit 22
"""

STUB_EFBUNDLE = """#!/usr/bin/env bash
printf 'efbundle %s\\n' "$*" >> "$STUB_LOG"
"""


def load_yaml_text(text):
    try:
        import yaml
    except ImportError:
        yaml = None
    if yaml is not None:
        return yaml.safe_load(text)
    if shutil.which("yq") is None:
        sys.exit("error: needs PyYAML or yq to read YAML")
    with tempfile.NamedTemporaryFile(
        "w", encoding="utf-8", suffix=".yml", delete=False
    ) as tmp:
        tmp.write(text)
    try:
        out = subprocess.run(
            ["yq", "-o=json", ".", tmp.name],
            capture_output=True, text=True, check=True,
        ).stdout
    finally:
        os.unlink(tmp.name)
    return json.loads(out)


ROOT = Path(__file__).resolve().parents[2]


def steps():
    workflow = load_yaml_text((ROOT / WORKFLOW).read_text(encoding="utf-8"))
    return workflow["jobs"][JOB]["steps"]


def step(name):
    found = [s for s in steps() if s.get("name") == name]
    if len(found) != 1:
        raise AssertionError(f"{WORKFLOW} has no single step named '{name}'")
    return found[0]


def write_script(path, text):
    path.write_text(text, encoding="utf-8", newline="\n")
    path.chmod(path.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)


class StepRun:
    def __init__(self, proc, log, outputs):
        self.code = proc.returncode
        self.stdout = proc.stdout
        self.stderr = proc.stderr
        self.calls = log
        self.outputs = outputs

    def az(self, prefix):
        return [c for c in self.calls if c.startswith(f"az {prefix}")]


class StepTest(unittest.TestCase):
    def setUp(self):
        self.bash = shutil.which("bash")
        if self.bash is None:
            self.skipTest("bash not found")
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)
        self.bin = self.tmp / "bin"
        self.bin.mkdir()
        write_script(self.bin / "az", STUB_AZ)
        write_script(self.bin / "curl", STUB_CURL)
        self.runner_temp = self.tmp / "runner-temp"
        self.runner_temp.mkdir()
        write_script(self.runner_temp / "efbundle", STUB_EFBUNDLE)

    def run_step(self, name, env):
        script = self.tmp / "step.sh"
        script.write_text(step(name)["run"], encoding="utf-8", newline="\n")
        log = self.tmp / "calls.log"
        output = self.tmp / "github-output"
        log.write_text("")
        output.write_text("")
        full_env = {k: v for k, v in os.environ.items() if not k.startswith("STUB_")}
        full_env.update(
            PATH=str(self.bin) + os.pathsep + os.environ.get("PATH", ""),
            STUB_LOG=log.as_posix(),
            GITHUB_OUTPUT=output.as_posix(),
            RUNNER_TEMP=self.runner_temp.as_posix(),
        )
        full_env.update(env)
        proc = subprocess.run(
            [self.bash, "--noprofile", "--norc", "-e", script.as_posix()],
            cwd=self.tmp, env=full_env, capture_output=True, text=True,
        )
        outputs = dict(
            line.split("=", 1)
            for line in output.read_text().splitlines()
            if "=" in line
        )
        return StepRun(proc, log.read_text().splitlines(), outputs)

    def assert_failed_with(self, run, *needles):
        self.assertNotEqual(run.code, 0, run.stdout + run.stderr)
        errors = [l for l in run.stdout.splitlines() if l.startswith("::error::")]
        self.assertTrue(errors, f"no ::error:: annotation in:\n{run.stdout}")
        for needle in needles:
            self.assertIn(needle, "\n".join(errors))


class OpenStepTest(StepTest):
    def open(self, **stubs):
        env = dict(WEBAPP_NAME=WEBAPP, RULE_NAME=RULE)
        defaults = dict(STUB_RG=RG, STUB_CONN=CONN, STUB_IPIFY="203.0.113.7")
        defaults.update(stubs)
        env.update({k: v for k, v in defaults.items() if v is not None})
        return self.run_step(OPEN, env)

    def test_adds_a_rule_for_the_runner_ip_on_the_databases_server(self):
        run = self.open()
        self.assertEqual(run.code, 0, run.stdout + run.stderr)
        self.assertEqual(
            run.az("sql server firewall-rule create"),
            [
                f"az sql server firewall-rule create --resource-group {RG} --server {SERVER} "
                f"--name {RULE} --start-ip-address 203.0.113.7 --end-ip-address 203.0.113.7 -o none"
            ],
        )
        self.assertEqual(
            run.outputs, {"resource-group": RG, "server": SERVER, "rule": RULE}
        )
        self.assertNotIn(PASSWORD, run.stdout + run.stderr)

    def test_missing_permission_fails_naming_it(self):
        run = self.open(STUB_CREATE_ERROR=DENIED)
        self.assert_failed_with(
            run, "Microsoft.Sql/servers/firewallRules/write", "SQL Server Contributor", SERVER
        )
        # No rule, so the close step is skipped.
        self.assertNotIn("rule", run.outputs)

    def test_other_create_failure_fails_without_blaming_the_role(self):
        run = self.open(STUB_CREATE_ERROR="ERROR: (ServerNotFound) The requested server was not found.")
        self.assert_failed_with(run, f"Could not add SQL firewall rule '{RULE}'")
        self.assertNotIn("firewallRules/write", run.stdout)
        self.assertNotIn("rule", run.outputs)

    def test_reads_the_server_from_other_connection_string_spellings(self):
        for conn in [
            f"server={SERVER}.database.windows.net;database=anchordb;password=x;",
            f"Data Source=tcp:{SERVER.upper()}.database.windows.net,1433;Initial Catalog=anchordb;",
            f"Encrypt=true; Server = tcp:{SERVER}.database.windows.net,1433 ;Password=x",
        ]:
            with self.subTest(conn=conn):
                run = self.open(STUB_CONN=conn)
                self.assertEqual(run.code, 0, run.stdout + run.stderr)
                self.assertEqual(run.outputs.get("server"), SERVER)

    def test_unreadable_server_fails_before_any_rule(self):
        run = self.open(STUB_CONN="")
        self.assert_failed_with(run, "SQL server name", WEBAPP)
        self.assertEqual(run.az("sql server firewall-rule"), [])

    def test_unknown_app_service_fails_before_any_rule(self):
        run = self.open(STUB_RG="")
        self.assert_failed_with(run, f"App Service '{WEBAPP}' not found")
        self.assertEqual(run.az("sql server firewall-rule"), [])

    def test_falls_back_to_the_second_ip_service(self):
        run = self.open(STUB_IPIFY=None, STUB_CHECKIP="198.51.100.9")
        self.assertEqual(run.code, 0, run.stdout + run.stderr)
        self.assertIn("--start-ip-address 198.51.100.9 --end-ip-address 198.51.100.9",
                      run.az("sql server firewall-rule create")[0])

    def test_no_ipv4_address_fails_before_any_rule(self):
        run = self.open(STUB_IPIFY="<html>Service Unavailable</html>", STUB_CHECKIP=None)
        self.assert_failed_with(run, "public IPv4 address")
        self.assertEqual(run.az("sql server firewall-rule"), [])


class MigrateStepTest(StepTest):
    def test_migrates_with_the_connection_string_from_the_rules_resource_group(self):
        run = self.run_step(
            MIGRATE, dict(WEBAPP_NAME=WEBAPP, RESOURCE_GROUP=RG, STUB_CONN=CONN)
        )
        self.assertEqual(run.code, 0, run.stdout + run.stderr)
        self.assertEqual(run.az("resource list"), [])
        self.assertTrue(
            run.az(f"webapp config connection-string list -g {RG} -n {WEBAPP} "),
            run.calls,
        )
        self.assertIn(f"efbundle --connection {CONN}", run.calls)
        self.assertIn(f"::add-mask::{CONN.replace('%', '%25')}", run.stdout)
        self.assertIn(f"::add-mask::{PASSWORD.replace('%', '%25')}", run.stdout)


class CloseStepTest(StepTest):
    ENV = dict(RESOURCE_GROUP=RG, SQL_SERVER=SERVER, RULE_NAME=RULE)

    def test_removes_the_rule(self):
        run = self.run_step(CLOSE, self.ENV)
        self.assertEqual(run.code, 0, run.stdout + run.stderr)
        self.assertEqual(
            run.az("sql server firewall-rule"),
            [f"az sql server firewall-rule delete --resource-group {RG} --server {SERVER} --name {RULE} -o none"],
        )

    def test_failed_removal_fails_with_the_command_to_run(self):
        run = self.run_step(CLOSE, dict(self.ENV, STUB_DELETE_ERROR="ERROR: (InternalServerError)"))
        self.assert_failed_with(
            run, f"az sql server firewall-rule delete -g {RG} -s {SERVER} -n {RULE}"
        )


class WiringTest(unittest.TestCase):
    def test_rule_is_opened_after_login_before_migrating_and_closed_last(self):
        names = [s.get("name") for s in steps()]
        for name in (LOGIN, OPEN, MIGRATE, DEPLOY, CLOSE):
            self.assertIn(name, names)
        self.assertLess(names.index(LOGIN), names.index(OPEN))
        self.assertLess(names.index(OPEN), names.index(MIGRATE))
        self.assertLess(names.index(MIGRATE), names.index(DEPLOY))
        self.assertEqual(names[-1], CLOSE)

    def test_open_step_names_the_rule_after_the_run(self):
        s = step(OPEN)
        self.assertEqual(s.get("id"), "sql-firewall")
        self.assertEqual(s["env"]["WEBAPP_NAME"], "${{ vars.AZURE_WEBAPP_NAME }}")
        self.assertIn("${{ github.run_id }}", s["env"]["RULE_NAME"])

    def test_migrations_use_the_resource_group_the_open_step_found(self):
        self.assertEqual(
            step(MIGRATE)["env"]["RESOURCE_GROUP"],
            "${{ steps.sql-firewall.outputs.resource-group }}",
        )

    def test_close_step_runs_always_once_a_rule_exists(self):
        s = step(CLOSE)
        condition = s.get("if", "").replace(" ", "")
        self.assertIn("always()", condition)
        self.assertIn("steps.sql-firewall.outputs.rule!=''", condition)
        self.assertEqual(
            s["env"],
            {
                "RESOURCE_GROUP": "${{ steps.sql-firewall.outputs.resource-group }}",
                "SQL_SERVER": "${{ steps.sql-firewall.outputs.server }}",
                "RULE_NAME": "${{ steps.sql-firewall.outputs.rule }}",
            },
        )


if __name__ == "__main__":
    if len(sys.argv) > 1:
        ROOT = Path(sys.argv.pop(1)).resolve()
    unittest.main(verbosity=2)
