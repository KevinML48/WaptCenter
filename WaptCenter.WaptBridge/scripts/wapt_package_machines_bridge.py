from __future__ import annotations

import argparse
import configparser
import json
import os
import sys
import traceback
from concurrent.futures import ThreadPoolExecutor, as_completed
from contextlib import ExitStack
from pathlib import Path

SCRIPT_DIRECTORY = Path(__file__).resolve().parent
if str(SCRIPT_DIRECTORY) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIRECTORY))

from wapt_packages_bridge import (
    build_excerpt,
    build_repo_url,
    format_command,
    first_non_empty,
    format_command_result,
    prepare_client_certificate_material,
    resolve_wapt_get_executable,
    run_wapt_command,
    temporary_wapt_config,
)


HOST_SEARCH_STRATEGY = "WaptServerHostsInstalledPackages"
HOST_DEPENDS_FALLBACK_STRATEGY = "WaptServerHostsDependsFallback"
HOSTS_FOR_PACKAGE_FALLBACK_STRATEGY = "WaptServerHostsForPackageFallback"
HOST_DATA_INSTALLED_PACKAGES_FALLBACK_STRATEGY = "WaptServerHostDataInstalledPackagesFallback"
EXACT_INSTALL_MATCH_TYPE = "exact_install"
DEPENDS_FALLBACK_MATCH_TYPE = "depends_fallback"
COMPLIANT_STATUS = "compliant"
UNKNOWN_COMPLIANCE_STATUS = "unknown"
NON_COMPLIANT_STATUS = "non_compliant"
UNKNOWN_OU_PATH = "OU non renseignee"
HOST_DATA_FIELD_INSTALLED_PACKAGES = "installed_packages"
HOSTS_FOR_PACKAGE_SAMPLE_LIMIT = 5
MAX_HOST_DATA_SAMPLE_COMMANDS = 3
MAX_HOST_DATA_SAMPLE_FAILURES = 5
MAX_HOST_DATA_WORKERS = 8
AUTHENTICATION_MODE_USED = (
    "WAPT CLI non-interactive basic auth via --wapt-server-user/--wapt-server-passwd "
    "with use_kerberos=0 in the temporary config"
)
AUTHENTICATION_FAILURE_MESSAGE = (
    "Les identifiants serveur WAPT pour l'inventaire machines ne sont pas acceptes ou ne sont pas transmis correctement."
)
DETAILED_INVENTORY_REQUIRED_MESSAGE = (
    "Le serveur WAPT ne renvoie pas les informations d'installation par machine avec la strategie actuelle. "
    "Un fallback d'inventaire plus detaille est requis."
)
NO_MACHINE_LINKED_MESSAGE = (
    "Aucune machine n'a pu etre reliee a ce package_id avec les strategies d'inventaire disponibles."
)
LIST_HOSTS_INVENTORY_COLUMNS = (
    "uuid,computer_fqdn,computer_name,host_status,last_seen_on,host_info,"
    "wapt_status,installed_packages,installed_packages_ids,depends"
)
INTERACTIVE_PROMPT_MARKERS = (
    "Admin User:",
    "Please provide username",
    "Please get login",
    "Password:",
    "TOTP Code:",
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Retrieve the machines where a WAPT package_id is installed via native WAPT tooling."
    )
    parser.add_argument("--server-url", required=True)
    parser.add_argument("--server-user", default="")
    parser.add_argument("--server-password", default="")
    parser.add_argument("--client-cert", default="")
    parser.add_argument("--client-key", default="")
    parser.add_argument("--client-pkcs12", default="")
    parser.add_argument("--password", default="")
    parser.add_argument("--ca-cert", default="")
    parser.add_argument("--timeout", type=int, default=30)
    parser.add_argument("--package-id", required=True)
    return parser.parse_args()


def apply_machine_inventory_config_overrides(config_path: Path) -> None:
    config = configparser.RawConfigParser()
    config.optionxform = str
    config.read(config_path, encoding="utf-8")

    if not config.has_section("global"):
        config.add_section("global")

    config.set("global", "use_kerberos", "0")

    with open(config_path, "w", encoding="utf-8") as stream:
        config.write(stream)


def build_list_hosts_command_arguments(args: argparse.Namespace) -> list[str]:
    return [
        "--not-interactive",
        f"--wapt-server-user={args.server_user}",
        f"--wapt-server-passwd={args.server_password}",
        "list-hosts",
        f"--columns={LIST_HOSTS_INVENTORY_COLUMNS}",
    ]


def build_server_request_command_arguments(
    args: argparse.Namespace,
    action: str,
    method: str = "GET",
) -> list[str]:
    return [
        "--not-interactive",
        f"--wapt-server-user={args.server_user}",
        f"--wapt-server-passwd={args.server_password}",
        "server-request",
        action,
        f"--method={method}",
    ]


def build_redacted_command(
    wapt_get_executable: Path,
    config_path: Path,
    command_arguments: list[str],
) -> str:
    redacted_arguments = []
    for argument in command_arguments:
        if argument.startswith("--wapt-server-passwd="):
            redacted_arguments.append("--wapt-server-passwd=<redacted>")
        else:
            redacted_arguments.append(argument)

    return format_command([str(wapt_get_executable), "-c", str(config_path), "-j", *redacted_arguments])


def as_dict(value: object) -> dict:
    return value if isinstance(value, dict) else {}


def ensure_dict_items(value: object) -> list[dict]:
    if isinstance(value, list):
        return [item for item in value if isinstance(item, dict)]

    if isinstance(value, dict):
        if isinstance(value.get("result"), list):
            return [item for item in value["result"] if isinstance(item, dict)]

        dict_values = [item for item in value.values() if isinstance(item, dict)]
        return dict_values

    return []


def extract_host_items(raw_payload: object) -> list[dict]:
    if isinstance(raw_payload, list):
        return [item for item in raw_payload if isinstance(item, dict)]

    if not isinstance(raw_payload, dict):
        return []

    for key in ("result", "rows", "hosts", "items", "data"):
        items = extract_host_items(raw_payload.get(key))
        if items:
            return items

    host_markers = ("computer_name", "computer_fqdn", "host_info", "uuid", "hostname")
    if any(marker in raw_payload for marker in host_markers):
        return [raw_payload]

    return []


def resolve_command_payload(command_result: dict) -> object:
    if command_result.get("json_payload") is not None:
        return command_result["json_payload"]

    stdout = (command_result.get("stdout") or "").strip()
    if not stdout:
        return None

    try:
        return json.loads(stdout)
    except Exception:
        return None


def get_payload_username(command_payload: object) -> str:
    if not isinstance(command_payload, dict):
        return ""

    username = command_payload.get("username")
    return str(username).strip() if username is not None else ""


def get_payload_usergroups(command_payload: object) -> list[str]:
    if not isinstance(command_payload, dict):
        return []

    usergroups = command_payload.get("usergroups")
    if not isinstance(usergroups, list):
        return []

    return [str(group).strip() for group in usergroups if str(group).strip()]


def build_raw_output_excerpt(command_result: dict, command_payload: object) -> str:
    if isinstance(command_payload, dict):
        output = command_payload.get("output")
        if output not in (None, ""):
            return build_excerpt(output)

    stdout = (command_result.get("stdout") or "").strip()
    if stdout:
        return build_excerpt(stdout)

    stderr = (command_result.get("stderr") or "").strip()
    if stderr:
        return build_excerpt(stderr)

    return "<none>"


def was_interactive_prompt_avoided(command_result: dict, command_payload: object) -> bool:
    combined_output = "\n".join(
        part
        for part in (
            command_result.get("stdout") or "",
            command_result.get("stderr") or "",
            command_payload.get("output") if isinstance(command_payload, dict) else "",
        )
        if part
    )
    combined_output = combined_output.lower()

    return not any(marker.lower() in combined_output for marker in INTERACTIVE_PROMPT_MARKERS)


def payload_indicates_authentication_failure(command_payload: object) -> bool:
    if not isinstance(command_payload, dict):
        return False

    http_status = command_payload.get("http_status")
    username = get_payload_username(command_payload)
    usergroups = get_payload_usergroups(command_payload)
    output_text = "\n".join(
        str(value)
        for value in (
            command_payload.get("result") or "",
            command_payload.get("output") or "",
            command_payload.get("headers") or "",
        )
        if value
    ).lower()

    if "unable to login with provided credentials" in output_text:
        return True

    if http_status == 401 and not username and not usergroups:
        return True

    return "requires ssl auth" in output_text and not username and not usergroups


def build_authentication_details(
    command_result: dict,
    command_payload: object,
    redacted_command: str,
    interactive_prompt_avoided: bool,
    server_user_provided: bool,
    server_password_provided: bool,
) -> list[str]:
    username = get_payload_username(command_payload)
    usergroups = get_payload_usergroups(command_payload)
    payload_action = command_payload.get("action") if isinstance(command_payload, dict) else None
    payload_http_status = command_payload.get("http_status") if isinstance(command_payload, dict) else None

    return [
        f"Server user provided: {'yes' if server_user_provided else 'no'}",
        f"Server password provided: {'yes' if server_password_provided else 'no'}",
        f"Authentication mode used: {AUTHENTICATION_MODE_USED}",
        f"Command launched: {redacted_command}",
        f"Interactive prompt avoided: {'yes' if interactive_prompt_avoided else 'no'}",
        f"Returned action: {payload_action or '<unknown>'}",
        f"Returned HTTP status: {payload_http_status if payload_http_status is not None else '<unknown>'}",
        f"Returned username: {username or '<empty>'}",
        f"Returned usergroups: {', '.join(usergroups) if usergroups else '[]'}",
        f"Raw output excerpt: {build_raw_output_excerpt(command_result, command_payload)}",
    ]


def extract_package_entries(host_item: dict) -> list[dict]:
    host_info = as_dict(host_item.get("host_info"))
    wapt_status = as_dict(host_item.get("wapt_status"))

    raw_candidates = [
        host_item.get("installed_packages"),
        host_item.get("packages"),
        host_item.get("package_status"),
        wapt_status.get("installed_packages"),
        wapt_status.get("packages"),
        host_info.get("installed_packages"),
        host_info.get("packages"),
    ]

    package_entries: list[dict] = []
    for candidate in raw_candidates:
        package_entries.extend(ensure_dict_items(candidate))

    if not package_entries and any(
        key in host_item for key in ("package_id", "package", "installed_version", "version", "status")
    ):
        package_entries.append(host_item)

    return package_entries


def package_entry_matches(package_entry: dict, package_id: str) -> bool:
    package_identifier = first_non_empty(package_entry, "package_id", "package")
    if package_identifier and package_identifier.lower() == package_id.lower():
        return True

    package_name = first_non_empty(package_entry, "name")
    return bool(package_name) and package_name.lower() == package_id.lower()


def extract_ou_from_dn(distinguished_name: str) -> str:
    if not distinguished_name:
        return ""

    organizational_units = []
    for segment in distinguished_name.split(","):
        segment = segment.strip()
        if segment.upper().startswith("OU="):
            organizational_units.append(segment[3:])

    return " / ".join(organizational_units)


def extract_groups(host_item: dict) -> list[str]:
    host_info = as_dict(host_item.get("host_info"))

    raw_candidates = (
        host_item.get("groups"),
        host_item.get("computer_ad_groups"),
        host_info.get("groups"),
        host_info.get("computer_ad_groups"),
    )

    for candidate in raw_candidates:
        if isinstance(candidate, list):
            return [str(item).strip() for item in candidate if str(item).strip()]

        if isinstance(candidate, tuple):
            return [str(item).strip() for item in candidate if str(item).strip()]

        if isinstance(candidate, str) and candidate.strip():
            return [candidate.strip()]

    return []


def build_organization_display(organization: str, ou_path: str) -> str:
    if organization and ou_path:
        return f"{organization} | {ou_path}"

    if organization:
        return organization

    return ou_path or UNKNOWN_OU_PATH


def resolve_compliance_status(match_type: str) -> str:
    if match_type == EXACT_INSTALL_MATCH_TYPE:
        return COMPLIANT_STATUS

    if match_type == DEPENDS_FALLBACK_MATCH_TYPE:
        return UNKNOWN_COMPLIANCE_STATUS

    return UNKNOWN_COMPLIANCE_STATUS


def normalize_machine(
    host_item: dict,
    package_entry: dict,
    package_id: str,
    match_type: str = EXACT_INSTALL_MATCH_TYPE,
    is_exact_install: bool = True,
) -> dict:
    host_info = as_dict(host_item.get("host_info"))
    wapt_status = as_dict(host_item.get("wapt_status"))

    fqdn = first_non_empty(host_item, "computer_fqdn", "fqdn", "host")
    if not fqdn:
        fqdn = first_non_empty(host_info, "computer_fqdn", "fqdn")

    hostname = first_non_empty(host_item, "computer_name", "hostname", "host_name", "name")
    if not hostname and fqdn:
        hostname = fqdn.split(".", 1)[0]

    last_seen = first_non_empty(host_item, "last_seen", "last_seen_on", "lastseen", "updated_on")
    if not last_seen:
        last_seen = first_non_empty(wapt_status, "last_seen", "last_update_status", "last_update")

    distinguished_name = first_non_empty(host_item, "computer_ad_dn", "ad_dn")
    if not distinguished_name:
        distinguished_name = first_non_empty(host_info, "computer_ad_dn", "ad_dn")

    organizational_unit = first_non_empty(host_item, "organizational_unit", "organization_unit", "ou")
    if not organizational_unit:
        organizational_unit = extract_ou_from_dn(distinguished_name)
    ou_path = organizational_unit or UNKNOWN_OU_PATH

    organization = first_non_empty(host_item, "organization", "registered_organization")
    if not organization:
        organization = first_non_empty(host_info, "registered_organization", "organization")
    groups = extract_groups(host_item)
    organization_display = build_organization_display(organization, ou_path)

    status = first_non_empty(package_entry, "status", "install_status", "state", "package_status")
    if not status:
        status = first_non_empty(host_item, "status", "host_status")

    installed_version = first_non_empty(package_entry, "installed_version", "version", "package_version")
    uuid = first_non_empty(host_item, "uuid", "host_uuid", "computer_uuid", "id")
    if not uuid:
        uuid = first_non_empty(host_info, "uuid", "id")

    return {
        "hostname": hostname,
        "fqdn": fqdn,
        "package_id": package_id,
        "installed_version": installed_version,
        "match_type": match_type,
        "is_exact_install": is_exact_install,
        "compliance_status": resolve_compliance_status(match_type),
        "status": status,
        "last_seen": last_seen,
        "organizational_unit": organizational_unit,
        "ou_path": ou_path,
        "organization": organization,
        "organization_display": organization_display,
        "groups": groups,
        "uuid": uuid,
    }


def deduplicate_machines(machines: list[dict]) -> list[dict]:
    seen_keys: set[str] = set()
    unique_machines: list[dict] = []

    for machine in machines:
        identity = machine.get("uuid") or machine.get("fqdn") or machine.get("hostname")
        deduplication_key = f"{identity}|{machine.get('package_id')}|{machine.get('installed_version')}"
        if deduplication_key in seen_keys:
            continue

        seen_keys.add(deduplication_key)
        unique_machines.append(machine)

    return unique_machines


def extract_result_wrapper(command_payload: object) -> dict:
    if not isinstance(command_payload, dict):
        return {}

    result_wrapper = command_payload.get("result")
    return result_wrapper if isinstance(result_wrapper, dict) else {}


def extract_result_message(command_payload: object) -> str:
    result_wrapper = extract_result_wrapper(command_payload)
    message = result_wrapper.get("msg") if result_wrapper else None
    return str(message).strip() if message is not None else ""


def extract_result_success(command_payload: object) -> bool:
    result_wrapper = extract_result_wrapper(command_payload)
    return result_wrapper.get("success") is True


def extract_result_items(command_payload: object) -> list[dict]:
    result_wrapper = extract_result_wrapper(command_payload)
    if not result_wrapper:
        return []

    result_items = result_wrapper.get("result")
    if isinstance(result_items, list):
        return [item for item in result_items if isinstance(item, dict)]

    if isinstance(result_items, dict):
        return [result_items]

    return []


def build_detected_structure_details(command_payload: object, host_items: list[dict]) -> list[str]:
    details: list[str] = []

    if isinstance(command_payload, dict):
        details.append(
            "Response root keys: " + ", ".join(sorted(command_payload.keys())[:30])
        )

    result_wrapper = extract_result_wrapper(command_payload)
    if result_wrapper:
        details.append(
            "Response result wrapper keys: " + ", ".join(sorted(result_wrapper.keys())[:30])
        )
        if result_wrapper.get("msg"):
            details.append(f"Response result message: {build_excerpt(result_wrapper['msg'])}")
        if result_wrapper.get("request_time") is not None:
            details.append(f"Response request time: {result_wrapper['request_time']}")

    if host_items:
        first_host = host_items[0]
        details.append("First host row keys: " + ", ".join(sorted(first_host.keys())[:40]))
        host_info = as_dict(first_host.get("host_info"))
        if host_info:
            details.append("First host_info keys: " + ", ".join(sorted(host_info.keys())[:40]))
        wapt_status = as_dict(first_host.get("wapt_status"))
        if wapt_status:
            details.append("First wapt_status keys: " + ", ".join(sorted(wapt_status.keys())[:40]))
        details.append("First host row excerpt: " + build_excerpt(first_host))

    return details


def count_hosts_with_field(host_items: list[dict], field_name: str) -> int:
    return sum(1 for host_item in host_items if field_name in host_item)


def split_depends_values(host_item: dict) -> list[str]:
    raw_depends = first_non_empty(host_item, "depends")
    if not raw_depends:
        return []

    return [item.strip() for item in raw_depends.split(",") if item.strip()]


def host_depends_on_package(host_item: dict, package_id: str) -> bool:
    package_id_lower = package_id.lower()
    return any(dependency.lower() == package_id_lower for dependency in split_depends_values(host_item))


def normalize_machine_from_depends(host_item: dict, package_id: str) -> dict:
    synthetic_package_entry = {
        "package_id": package_id,
        "installed_version": "",
        "status": first_non_empty(host_item, "host_status", "status") or "DependencyDeclared",
    }
    return normalize_machine(
        host_item,
        synthetic_package_entry,
        package_id,
        match_type=DEPENDS_FALLBACK_MATCH_TYPE,
        is_exact_install=False,
    )


def normalize_machine_from_hosts_for_package(package_row: dict, package_id: str) -> dict:
    host_item = {
        "uuid": first_non_empty(package_row, "host", "uuid", "host_uuid"),
        "computer_name": first_non_empty(package_row, "host_computer_name", "computer_name", "hostname"),
        "computer_fqdn": first_non_empty(package_row, "host_computer_fqdn", "computer_fqdn", "fqdn"),
        "host_status": first_non_empty(package_row, "reachable", "host_status", "status") or "OK",
        "last_seen_on": first_non_empty(package_row, "listening_timestamp", "updated_on", "last_audit_on", "install_date"),
        "organization": first_non_empty(package_row, "registered_organization", "organization"),
        "host_info": {
            "computer_name": first_non_empty(package_row, "host_computer_name", "computer_name", "hostname"),
            "computer_fqdn": first_non_empty(package_row, "host_computer_fqdn", "computer_fqdn", "fqdn"),
            "computer_ad_dn": first_non_empty(package_row, "host_computer_ad_dn", "computer_ad_dn", "ad_dn"),
            "registered_organization": first_non_empty(package_row, "registered_organization", "organization"),
        },
    }

    return normalize_machine(host_item, package_row, package_id)


def scan_hosts_for_package(
    args: argparse.Namespace,
    wapt_get_executable: Path,
    config_path: Path,
) -> tuple[list[dict], list[str], bool]:
    action = f"api/v3/hosts_for_package?package={args.package_id}"
    command_arguments = build_server_request_command_arguments(args, action)
    redacted_command = build_redacted_command(wapt_get_executable, config_path, command_arguments)
    command_result = run_wapt_command(
        wapt_get_executable,
        config_path,
        command_arguments,
        max(15, min(60, args.timeout)),
    )
    command_payload = resolve_command_payload(command_result)
    result_items = extract_result_items(command_payload)
    auth_failure_detected = payload_indicates_authentication_failure(command_payload)

    details = [
        (
            f"Strategy [{HOSTS_FOR_PACKAGE_FALLBACK_STRATEGY}] reason: api/v3/hosts_for_package est interroge directement "
            "car ni installed_packages dans list-hosts ni depends n'ont permis de relier ce package_id."
        )
    ]
    details.extend(
        build_authentication_details(
            command_result,
            command_payload,
            redacted_command,
            was_interactive_prompt_avoided(command_result, command_payload),
            bool(args.server_user),
            bool(args.server_password),
        )
    )

    sanitized_command_result = dict(command_result)
    sanitized_command_result["command"] = redacted_command
    details.append(format_command_result("hosts_for_package", sanitized_command_result))
    details.extend(build_detected_structure_details(command_payload, result_items))
    details.append(f"Strategy [{HOSTS_FOR_PACKAGE_FALLBACK_STRATEGY}] rows analysed: {len(result_items)}")

    if result_items:
        details.append(
            "Strategy [%s] first result excerpt: %s"
            % (HOSTS_FOR_PACKAGE_FALLBACK_STRATEGY, build_excerpt(result_items[0]))
        )

    if not command_result.get("ok") or (isinstance(command_payload, dict) and command_payload.get("http_status") != 200):
        details.append(
            f"Strategy [{HOSTS_FOR_PACKAGE_FALLBACK_STRATEGY}] endpoint call failed before row filtering."
        )
        return [], details, auth_failure_detected

    machines = deduplicate_machines(
        [
            normalize_machine_from_hosts_for_package(package_row, args.package_id)
            for package_row in result_items
            if package_entry_matches(package_row, args.package_id)
        ]
    )
    details.append(
        f"Strategy [{HOSTS_FOR_PACKAGE_FALLBACK_STRATEGY}] machines matching package_id exactly: {len(machines)}"
    )

    for index, package_row in enumerate(result_items[:HOSTS_FOR_PACKAGE_SAMPLE_LIMIT], start=1):
        details.append(
            f"Strategy [{HOSTS_FOR_PACKAGE_FALLBACK_STRATEGY}] sample row #{index}: {build_excerpt(package_row)}"
        )

    return machines, details, auth_failure_detected


def resolve_host_uuid(host_item: dict) -> str:
    host_info = as_dict(host_item.get("host_info"))
    return first_non_empty(host_item, "uuid", "host_uuid", "computer_uuid", "id") or first_non_empty(
        host_info,
        "uuid",
        "id",
    )


def resolve_host_identity(host_item: dict) -> str:
    return first_non_empty(host_item, "computer_name", "computer_fqdn", "uuid") or "<unknown-host>"


def build_host_data_action(host_uuid: str, field_name: str = HOST_DATA_FIELD_INSTALLED_PACKAGES) -> str:
    return f"api/v3/host_data?uuid={host_uuid}&field={field_name}"


def resolve_host_data_timeout_seconds(timeout_seconds: int) -> int:
    return max(10, min(20, timeout_seconds))


def resolve_host_data_max_workers(host_count: int) -> int:
    return max(1, min(MAX_HOST_DATA_WORKERS, host_count, os.cpu_count() or MAX_HOST_DATA_WORKERS))


def probe_host_data_installed_packages(
    args: argparse.Namespace,
    wapt_get_executable: Path,
    config_path: Path,
    host_item: dict,
    timeout_seconds: int,
) -> dict:
    host_uuid = resolve_host_uuid(host_item)
    action = build_host_data_action(host_uuid)
    command_arguments = build_server_request_command_arguments(args, action)
    redacted_command = build_redacted_command(wapt_get_executable, config_path, command_arguments)
    command_result = run_wapt_command(
        wapt_get_executable,
        config_path,
        command_arguments,
        timeout_seconds,
    )
    command_payload = resolve_command_payload(command_result)

    return {
        "host_uuid": host_uuid,
        "host_identity": resolve_host_identity(host_item),
        "host_item": host_item,
        "redacted_command": redacted_command,
        "command_result": command_result,
        "command_payload": command_payload,
        "result_success": extract_result_success(command_payload),
        "result_message": extract_result_message(command_payload),
        "result_items": extract_result_items(command_payload),
        "http_status": command_payload.get("http_status") if isinstance(command_payload, dict) else None,
    }


def scan_host_data_installed_packages(
    args: argparse.Namespace,
    wapt_get_executable: Path,
    config_path: Path,
    host_items: list[dict],
    timeout_seconds: int,
) -> tuple[list[dict], list[str], bool]:
    details = [
        (
            f"Strategy [{HOST_DATA_INSTALLED_PACKAGES_FALLBACK_STRATEGY}] reason: api/v3/host_data?field={HOST_DATA_FIELD_INSTALLED_PACKAGES} "
            "est interroge hote par hote car ni installed_packages ni depends n'ont permis de relier ce package_id."
        )
    ]

    eligible_hosts = [host_item for host_item in host_items if resolve_host_uuid(host_item)]
    details.append(
        f"Strategy [{HOST_DATA_INSTALLED_PACKAGES_FALLBACK_STRATEGY}] hosts eligible for detailed scan: {len(eligible_hosts)}"
    )

    if not eligible_hosts:
        details.append(
            f"Strategy [{HOST_DATA_INSTALLED_PACKAGES_FALLBACK_STRATEGY}] no eligible host UUID was available for detailed scan."
        )
        return [], details, False

    host_data_timeout_seconds = resolve_host_data_timeout_seconds(timeout_seconds)
    max_workers = resolve_host_data_max_workers(len(eligible_hosts))
    details.append(
        f"Strategy [{HOST_DATA_INSTALLED_PACKAGES_FALLBACK_STRATEGY}] timeout per host_data request: {host_data_timeout_seconds} second(s)"
    )
    details.append(
        f"Strategy [{HOST_DATA_INSTALLED_PACKAGES_FALLBACK_STRATEGY}] parallel host_data workers: {max_workers}"
    )

    machines: list[dict] = []
    sample_commands: list[str] = []
    sample_failures: list[str] = []
    commands_launched = 0
    successful_host_data_payloads = 0
    failed_host_data_payloads = 0
    auth_failure_detected = False

    with ThreadPoolExecutor(max_workers=max_workers) as executor:
        futures = [
            executor.submit(
                probe_host_data_installed_packages,
                args,
                wapt_get_executable,
                config_path,
                host_item,
                host_data_timeout_seconds,
            )
            for host_item in eligible_hosts
        ]

        for future in as_completed(futures):
            probe_result = future.result()
            commands_launched += 1

            redacted_command = probe_result["redacted_command"]
            if len(sample_commands) < MAX_HOST_DATA_SAMPLE_COMMANDS:
                sample_commands.append(redacted_command)

            command_result = probe_result["command_result"]
            command_payload = probe_result["command_payload"]
            result_items = probe_result["result_items"]
            result_success = probe_result["result_success"]
            result_message = probe_result["result_message"]
            http_status = probe_result["http_status"]

            if payload_indicates_authentication_failure(command_payload):
                auth_failure_detected = True

            if command_result.get("ok") and http_status == 200 and result_success:
                successful_host_data_payloads += 1
            else:
                failed_host_data_payloads += 1
                if len(sample_failures) < MAX_HOST_DATA_SAMPLE_FAILURES:
                    failure_excerpt = build_excerpt(result_message or build_raw_output_excerpt(command_result, command_payload))
                    sample_failures.append(
                        f"{probe_result['host_identity']} ({probe_result['host_uuid']}): HTTP {http_status if http_status is not None else '<unknown>'}, success={result_success}, detail={failure_excerpt}"
                    )

            if not command_result.get("ok") or http_status != 200 or not result_success:
                continue

            for package_entry in result_items:
                if package_entry_matches(package_entry, args.package_id):
                    machines.append(normalize_machine(probe_result["host_item"], package_entry, args.package_id))

    details.append(
        f"Strategy [{HOST_DATA_INSTALLED_PACKAGES_FALLBACK_STRATEGY}] detailed host_data commands launched: {commands_launched}"
    )
    details.append(
        f"Strategy [{HOST_DATA_INSTALLED_PACKAGES_FALLBACK_STRATEGY}] successful host_data payloads: {successful_host_data_payloads}"
    )
    details.append(
        f"Strategy [{HOST_DATA_INSTALLED_PACKAGES_FALLBACK_STRATEGY}] failed host_data payloads: {failed_host_data_payloads}"
    )

    for index, command in enumerate(sample_commands, start=1):
        details.append(
            f"Strategy [{HOST_DATA_INSTALLED_PACKAGES_FALLBACK_STRATEGY}] sample command #{index}: {command}"
        )

    for index, failure in enumerate(sample_failures, start=1):
        details.append(
            f"Strategy [{HOST_DATA_INSTALLED_PACKAGES_FALLBACK_STRATEGY}] sample failure #{index}: {failure}"
        )

    machines = deduplicate_machines(machines)
    details.append(
        f"Strategy [{HOST_DATA_INSTALLED_PACKAGES_FALLBACK_STRATEGY}] machines matching package_id via host_data: {len(machines)}"
    )

    return machines, details, auth_failure_detected


def build_response(success: bool, message: str, machines: list[dict], technical_details: list[str]) -> dict:
    return {
        "success": success,
        "message": message,
        "machines": machines,
        "technical_details": "\n".join(line for line in technical_details if line).strip(),
    }


def execute_bridge(args: argparse.Namespace) -> dict:
    technical_details = [
        "Bridge strategy: native WAPT CLI hosts inventory via Python bridge",
        f"Initial strategy: {HOST_SEARCH_STRATEGY}",
        f"Package_id requested: {args.package_id}",
        f"Server user provided: {'yes' if args.server_user else 'no'}",
        "LocalServiceMachineContext: not used for this feature because cross-machine inventory requires server-side hosts data.",
    ]

    if not args.server_user or not args.server_password:
        return build_response(
            False,
            "Les identifiants serveur WAPT sont requis pour charger les machines d'un paquet.",
            [],
            technical_details,
        )

    timeout_seconds = max(1, args.timeout)

    with ExitStack() as stack:
        wapt_get_executable = resolve_wapt_get_executable()
        technical_details.append(f"wapt-get executable: {wapt_get_executable}")

        client_material = prepare_client_certificate_material(args, stack)
        inventory_client_material = client_material if not args.client_pkcs12 else None
        repo_url = build_repo_url(args.server_url)

        with temporary_wapt_config(args, wapt_get_executable, repo_url, inventory_client_material) as config_path:
            apply_machine_inventory_config_overrides(config_path)
            technical_details.append("Temporary config override: use_kerberos=0")
            technical_details.append(
                "List-hosts transport variant: native config with PKCS12/personal_certificate_path"
                if inventory_client_material is None
                else "List-hosts transport variant: explicit PEM client_certificate/client_private_key"
            )

            command_arguments = build_list_hosts_command_arguments(args)
            redacted_command = build_redacted_command(wapt_get_executable, config_path, command_arguments)

            command_result = run_wapt_command(
                wapt_get_executable,
                config_path,
                command_arguments,
                timeout_seconds,
            )
            command_payload = resolve_command_payload(command_result)
            interactive_prompt_avoided = was_interactive_prompt_avoided(command_result, command_payload)

            sanitized_command_result = dict(command_result)
            sanitized_command_result["command"] = redacted_command

            technical_details.extend(
                build_authentication_details(
                    command_result,
                    command_payload,
                    redacted_command,
                    interactive_prompt_avoided,
                    bool(args.server_user),
                    bool(args.server_password),
                )
            )
            technical_details.append(format_command_result("list-hosts", sanitized_command_result))

            if payload_indicates_authentication_failure(command_payload):
                return build_response(
                    False,
                    AUTHENTICATION_FAILURE_MESSAGE,
                    [],
                    technical_details,
                )

            if not command_result["ok"]:
                return build_response(
                    False,
                    "La requete WAPT native list-hosts a echoue.",
                    [],
                    technical_details,
                )

            host_items = extract_host_items(command_payload)
            technical_details.append(f"Strategy [{HOST_SEARCH_STRATEGY}] command columns: {LIST_HOSTS_INVENTORY_COLUMNS}")
            technical_details.append(f"Hosts analysed: {len(host_items)}")
            technical_details.extend(build_detected_structure_details(command_payload, host_items))

            machines: list[dict] = []
            package_data_detected = False
            first_package_entry_keys = ""
            installed_packages_field_present_count = count_hosts_with_field(host_items, "installed_packages")
            installed_packages_ids_field_present_count = count_hosts_with_field(host_items, "installed_packages_ids")
            depends_field_present_count = count_hosts_with_field(host_items, "depends")

            for host_item in host_items:
                package_entries = extract_package_entries(host_item)
                if package_entries:
                    package_data_detected = True
                    if not first_package_entry_keys:
                        first_package_entry_keys = ", ".join(sorted(package_entries[0].keys())[:20])

                for package_entry in package_entries:
                    if package_entry_matches(package_entry, args.package_id):
                        machines.append(normalize_machine(host_item, package_entry, args.package_id))

            technical_details.append(
                f"Hosts exposing installed_packages field: {installed_packages_field_present_count}"
            )
            technical_details.append(
                f"Hosts exposing installed_packages_ids field: {installed_packages_ids_field_present_count}"
            )
            technical_details.append(f"Hosts exposing depends field: {depends_field_present_count}")
            technical_details.append(f"Installed package details detected: {package_data_detected}")
            if first_package_entry_keys:
                technical_details.append(f"First package entry keys: {first_package_entry_keys}")

            machines = deduplicate_machines(machines)
            technical_details.append(f"Machines matching package_id exactly: {len(machines)}")

            if machines:
                technical_details.append(f"Selected strategy: {HOST_SEARCH_STRATEGY}")
                return build_response(
                    True,
                    f"{len(machines)} machine(s) ont ete trouvees pour le package_id '{args.package_id}'.",
                    machines,
                    technical_details,
                )

            if host_items and not package_data_detected:
                technical_details.append(
                    "Absence reason: installed_packages / installed_packages_ids sont absents ou vides dans la reponse hosts, meme lorsqu'ils sont demandes dans --columns."
                )

                fallback_machines = [
                    normalize_machine_from_depends(host_item, args.package_id)
                    for host_item in host_items
                    if host_depends_on_package(host_item, args.package_id)
                ]
                fallback_machines = deduplicate_machines(fallback_machines)
                technical_details.append(
                    f"Strategy [{HOST_DEPENDS_FALLBACK_STRATEGY}] reason: depends est utilise comme fallback car le serveur n'expose pas installed_packages par machine dans list-hosts."
                )
                technical_details.append(
                    f"Machines matching package_id in depends fallback: {len(fallback_machines)}"
                )

                if fallback_machines:
                    technical_details.append(f"Selected strategy: {HOST_DEPENDS_FALLBACK_STRATEGY}")
                    return build_response(
                        True,
                        (
                            f"{len(fallback_machines)} machine(s) ont ete trouvees pour le package_id '{args.package_id}' "
                            "via le fallback depends. La version installee n'est pas exposee par le serveur."
                        ),
                        fallback_machines,
                        technical_details,
                    )

                hosts_for_package_machines, hosts_for_package_details, hosts_for_package_auth_failure = scan_hosts_for_package(
                    args,
                    wapt_get_executable,
                    config_path,
                )
                technical_details.extend(hosts_for_package_details)

                if hosts_for_package_auth_failure:
                    return build_response(
                        False,
                        AUTHENTICATION_FAILURE_MESSAGE,
                        [],
                        technical_details,
                    )

                if hosts_for_package_machines:
                    technical_details.append(f"Selected strategy: {HOSTS_FOR_PACKAGE_FALLBACK_STRATEGY}")
                    return build_response(
                        True,
                        (
                            f"{len(hosts_for_package_machines)} machine(s) ont ete trouvees pour le package_id '{args.package_id}' "
                            "via le fallback direct hosts_for_package."
                        ),
                        hosts_for_package_machines,
                        technical_details,
                    )

                host_data_machines, host_data_details, host_data_auth_failure = scan_host_data_installed_packages(
                    args,
                    wapt_get_executable,
                    config_path,
                    host_items,
                    timeout_seconds,
                )
                technical_details.extend(host_data_details)

                if host_data_auth_failure:
                    return build_response(
                        False,
                        AUTHENTICATION_FAILURE_MESSAGE,
                        [],
                        technical_details,
                    )

                if host_data_machines:
                    technical_details.append(f"Selected strategy: {HOST_DATA_INSTALLED_PACKAGES_FALLBACK_STRATEGY}")
                    return build_response(
                        True,
                        (
                            f"{len(host_data_machines)} machine(s) ont ete trouvees pour le package_id '{args.package_id}' "
                            "via le fallback host_data installed_packages."
                        ),
                        host_data_machines,
                        technical_details,
                    )

                technical_details.append(
                    "Absence reason: ni installed_packages dans list-hosts, ni depends exploitable, ni l'endpoint direct api/v3/hosts_for_package, ni installed_packages detaille via api/v3/host_data n'ont permis de relier ce package_id."
                )

                return build_response(
                    False,
                    NO_MACHINE_LINKED_MESSAGE,
                    [],
                    technical_details,
                )

            if not machines:
                return build_response(
                    True,
                    f"Aucune machine n'a ete trouvee pour le package_id '{args.package_id}'.",
                    [],
                    technical_details,
                )


def main() -> int:
    try:
        response = execute_bridge(parse_args())
    except Exception as exception:
        response = build_response(
            False,
            f"Le bridge machines WAPT a echoue: {exception}",
            [],
            [traceback.format_exc().strip(), f"Exception excerpt: {build_excerpt(exception)}"],
        )

    json.dump(response, sys.stdout, ensure_ascii=True)
    sys.stdout.flush()
    return 0 if response.get("success") else 1


if __name__ == "__main__":
    raise SystemExit(main())
