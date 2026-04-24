from __future__ import annotations

import argparse
import base64
import configparser
import datetime
import getpass
import json
import locale
import os
import secrets
import shlex
import subprocess
import sys
import tempfile
import time
import traceback
import uuid
from contextlib import ExitStack, contextmanager
from pathlib import Path
from urllib.parse import urlparse, urlunparse


LOCAL_SERVICE_BASE_URL = "https://127.0.0.1:8088"
LOCAL_SERVICE_MACHINE_TASK_NAME = "WaptCenter.LocalServiceMachineCatalog"
LOCAL_SERVICE_MACHINE_OUTPUT_MAX_AGE_SECONDS = 900
LOCAL_SERVICE_PING_ENDPOINT = "/ping"
LOCAL_SERVICE_CATALOG_ENDPOINTS = [
    "/packages.json?latest=1&all_sections=1&limit=5000",
    "/packages?latest=1&all_sections=1&limit=5000",
    "/list?latest=1&all_sections=1&limit=5000",
]
LOCAL_SERVICE_INFO_ENDPOINTS = [
    "/status.json",
    "/checkupgrades.json",
]
LOCAL_SERVICE_MACHINE_BRIDGE_DIR = (
    Path(os.environ.get("ProgramData", r"C:\ProgramData"))
    / "WaptCenter"
    / "MachineBridge"
)
FILTER_FIELD_USED = "package_id"
FILTER_MODE_USED = "contains"
MIN_UPDATE_TIMEOUT_SECONDS = 120
UPDATE_TIMEOUT_MULTIPLIER = 3
UPDATE_TIMEOUT_MESSAGE = (
    "La mise a jour de l'index WAPT a depasse le delai autorise. Augmentez le timeout."
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="WAPT package bridge")
    parser.add_argument("--server-url", required=True)
    parser.add_argument("--client-cert", default="")
    parser.add_argument("--client-key", default="")
    parser.add_argument("--client-pkcs12", default="")
    parser.add_argument("--password", default="")
    parser.add_argument("--ca-cert", default="")
    parser.add_argument("--timeout", type=int, default=30)
    parser.add_argument("--prefix", required=True)
    parser.add_argument("--use-existing-machine-output", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    response = execute_bridge(args)
    json.dump(response, sys.stdout, ensure_ascii=True)
    sys.stdout.write("\n")
    return 0 if response["success"] else 1


def resolve_update_timeout_seconds(configured_timeout_seconds: int) -> int:
    safe_timeout_seconds = max(1, configured_timeout_seconds)
    return max(MIN_UPDATE_TIMEOUT_SECONDS, safe_timeout_seconds * UPDATE_TIMEOUT_MULTIPLIER)


def execute_bridge(args: argparse.Namespace) -> dict:
    technical_details: list[str] = []

    try:
        wapt_get_executable = resolve_wapt_get_executable()
        repo_url = build_repo_url(args.server_url)
        configured_timeout_seconds = max(1, args.timeout)
        update_timeout_seconds = resolve_update_timeout_seconds(configured_timeout_seconds)

        technical_details.append(
            "Strategy: native WAPT bridge (update -> LocalServiceMachineContext -> LocalServiceCatalog -> LocalServiceBearer -> WaptRemoteRepo -> local-request -> search fallbacks)"
        )
        technical_details.append(
            "Strategy order: LocalServiceMachineContext -> LocalServiceCatalog -> LocalServiceBearer -> WaptRemoteRepo -> local-request packages.json -> search -> search -F 0"
        )
        technical_details.append(f"Python executable: {sys.executable}")
        technical_details.append(f"WAPT executable: {wapt_get_executable}")
        technical_details.append(f"Server URL: {args.server_url}")
        technical_details.append(f"Repository URL: {repo_url}")
        technical_details.append(
            f"Client PEM certificate: {args.client_cert if args.client_cert else '<none>'}"
        )
        technical_details.append(
            f"Client PEM private key: {args.client_key if args.client_key else '<none>'}"
        )
        technical_details.append(
            f"Client PKCS12: {args.client_pkcs12 if args.client_pkcs12 else '<none>'}"
        )
        technical_details.append(
            f"CA certificate: {args.ca_cert if args.ca_cert else '<unchanged from base config>'}"
        )
        technical_details.append(f"Timeout configured: {configured_timeout_seconds} second(s)")
        technical_details.append(f"Timeout used for update: {update_timeout_seconds} second(s)")
        technical_details.append(f"Filter field used: {FILTER_FIELD_USED}")
        technical_details.append(f"Filter mode used: {FILTER_MODE_USED}")
        technical_details.append(f"Filter value used: {args.prefix}")

        with ExitStack() as stack:
            client_material = None
            try:
                client_material = prepare_client_certificate_material(args, stack)
            except Exception:
                technical_details.append("Direct repository client certificate preparation failed:")
                technical_details.append(traceback.format_exc().strip())

            if client_material is not None:
                technical_details.append(
                    f"Repository client auth source: {client_material['source']}"
                )
                technical_details.append(
                    f"Repository client certificate: {client_material['certificate_path']}"
                )
                technical_details.append(
                    f"Repository client private key: {client_material['private_key_path']}"
                )
            else:
                technical_details.append("Repository client auth source: <none>")

            with temporary_wapt_config(
                args,
                wapt_get_executable,
                repo_url,
                client_material,
            ) as config_path:
                technical_details.append(f"Temporary config: {config_path}")

                update_result = run_wapt_command(
                    wapt_get_executable,
                    config_path,
                    ["update"],
                    timeout_seconds=update_timeout_seconds,
                )
                technical_details.append(format_command_result("update", update_result))

                update_package_count = extract_update_package_count(update_result["json_payload"])
                if update_package_count is not None:
                    technical_details.append(
                        f"Update reported repository count: {update_package_count}"
                    )

                if not update_result["ok"]:
                    return build_response(
                        success=False,
                        message=(
                            UPDATE_TIMEOUT_MESSAGE
                            if update_result["timed_out"]
                            else "Le client WAPT natif n'a pas pu mettre a jour l'index des paquets."
                        ),
                        packages=[],
                        technical_details=technical_details,
                    )

                best_total_count = update_package_count or 0

                local_service_machine_context_result = try_local_service_machine_context(
                    wapt_get_executable=wapt_get_executable,
                    timeout_seconds=max(1, args.timeout),
                    prefix=args.prefix,
                    use_existing_output=args.use_existing_machine_output,
                )
                machine_output_requires_refresh = local_service_machine_context_result[
                    "legacy_output_requires_refresh"
                ]
                technical_details.append(
                    format_local_service_machine_context_result(
                        local_service_machine_context_result,
                        args.prefix,
                    )
                )
                best_total_count = max(
                    best_total_count,
                    local_service_machine_context_result["max_total_count"],
                )

                if local_service_machine_context_result["ok"]:
                    technical_details.append(
                        build_final_strategy_summary(
                            strategy="LocalServiceMachineContext",
                            total_count=local_service_machine_context_result["total_count"],
                            matched_packages=local_service_machine_context_result["matched_packages"],
                            prefix=args.prefix,
                        )
                    )
                    if local_service_machine_context_result["matched_packages"]:
                        return build_response(
                            success=True,
                            message=build_match_message(
                                len(local_service_machine_context_result["matched_packages"]),
                                args.prefix,
                                "via le helper machine du service WAPT local",
                            ),
                            packages=local_service_machine_context_result["matched_packages"],
                            technical_details=technical_details,
                        )

                    return build_response(
                        success=True,
                        message=build_no_match_message(args.prefix),
                        packages=[],
                        technical_details=technical_details,
                    )

                local_service_catalog_result = try_local_service_catalog(
                    wapt_get_executable=wapt_get_executable,
                    timeout_seconds=max(1, args.timeout),
                    prefix=args.prefix,
                )
                technical_details.append(
                    format_local_service_catalog_result(local_service_catalog_result, args.prefix)
                )
                best_total_count = max(
                    best_total_count,
                    local_service_catalog_result["max_total_count"],
                )

                if local_service_catalog_result["ok"]:
                    technical_details.append(
                        build_final_strategy_summary(
                            strategy="LocalServiceCatalog",
                            total_count=local_service_catalog_result["total_count"],
                                matched_packages=local_service_catalog_result["matched_packages"],
                            prefix=args.prefix,
                        )
                    )
                    if local_service_catalog_result["matched_packages"]:
                        return build_response(
                            success=True,
                            message=build_match_message(
                                len(local_service_catalog_result["matched_packages"]),
                                args.prefix,
                                "via le catalogue du service WAPT local",
                            ),
                            packages=local_service_catalog_result["matched_packages"],
                            technical_details=technical_details,
                        )

                    return build_response(
                        success=True,
                        message=build_no_match_message(args.prefix),
                        packages=[],
                        technical_details=technical_details,
                    )

                if local_service_catalog_result["requires_bearer"]:
                    local_service_bearer_result = try_local_service_bearer(
                        wapt_get_executable=wapt_get_executable,
                        timeout_seconds=max(1, args.timeout),
                        prefix=args.prefix,
                    )
                    technical_details.append(
                        format_local_service_bearer_result(local_service_bearer_result, args.prefix)
                    )
                    best_total_count = max(
                        best_total_count,
                        local_service_bearer_result["max_total_count"],
                    )

                    if local_service_bearer_result["ok"]:
                        technical_details.append(
                            build_final_strategy_summary(
                                strategy="LocalServiceBearer",
                                total_count=local_service_bearer_result["total_count"],
                                matched_packages=local_service_bearer_result["matched_packages"],
                                prefix=args.prefix,
                            )
                        )
                        if local_service_bearer_result["matched_packages"]:
                            return build_response(
                                success=True,
                                message=build_match_message(
                                    len(local_service_bearer_result["matched_packages"]),
                                    args.prefix,
                                    "via le service WAPT local avec Bearer",
                                ),
                                packages=local_service_bearer_result["matched_packages"],
                                technical_details=technical_details,
                            )

                        return build_response(
                            success=True,
                            message=build_no_match_message(args.prefix),
                            packages=[],
                            technical_details=technical_details,
                        )

                repo_result = try_load_packages_from_wapt_repo(
                    repo_url=repo_url,
                    verify_cert=resolve_repo_verify_cert(args, wapt_get_executable),
                    timeout_seconds=max(1, args.timeout),
                    client_material=client_material,
                )
                technical_details.append(
                    format_direct_repo_result(repo_result, args.prefix)
                )

                if repo_result["ok"]:
                    repo_packages = repo_result["packages"]
                    repo_matches = filter_packages(repo_packages, args.prefix)
                    best_total_count = max(best_total_count, len(repo_packages))

                    if repo_packages:
                        technical_details.append(
                            build_final_strategy_summary(
                                strategy="WaptRemoteRepo",
                                total_count=len(repo_packages),
                                matched_packages=repo_matches,
                                prefix=args.prefix,
                            )
                        )

                        if repo_matches:
                            return build_response(
                                success=True,
                                message=build_match_message(
                                    len(repo_matches),
                                    args.prefix,
                                    "via l'API Python native WAPT",
                                ),
                                packages=repo_matches,
                                technical_details=technical_details,
                            )

                        return build_response(
                            success=True,
                            message=build_no_match_message(args.prefix),
                            packages=[],
                            technical_details=technical_details,
                        )

                local_service_result = run_wapt_command(
                    wapt_get_executable,
                    config_path,
                    ["local-request", "packages.json?latest=1&all_sections=1&limit=5000"],
                    timeout_seconds=max(30, args.timeout),
                )
                technical_details.append(
                    format_command_result("local-request packages.json", local_service_result)
                )

                local_service_packages = normalize_packages(
                    extract_package_items(local_service_result["json_payload"])
                )
                local_service_matches = filter_packages(local_service_packages, args.prefix)
                best_total_count = max(best_total_count, len(local_service_packages))
                technical_details.append(
                    build_package_source_summary(
                        label="local-request packages.json",
                        total_count=len(local_service_packages),
                        matched_packages=local_service_matches,
                        prefix=args.prefix,
                    )
                )

                if local_service_matches:
                    technical_details.append(
                        build_final_strategy_summary(
                            strategy="local-request packages.json",
                            total_count=len(local_service_packages),
                            matched_packages=local_service_matches,
                            prefix=args.prefix,
                        )
                    )
                    return build_response(
                        success=True,
                        message=build_match_message(
                            len(local_service_matches),
                            args.prefix,
                            "via le service WAPT local",
                        ),
                        packages=local_service_matches,
                        technical_details=technical_details,
                    )

                search_result = run_wapt_command(
                    wapt_get_executable,
                    config_path,
                    ["-n", "search", args.prefix],
                    timeout_seconds=max(30, args.timeout),
                )
                technical_details.append(format_command_result("search", search_result))

                search_packages = normalize_packages(
                    extract_package_items(search_result["json_payload"])
                )
                search_matches = filter_packages(search_packages, args.prefix)
                best_total_count = max(best_total_count, len(search_packages))
                technical_details.append(
                    build_package_source_summary(
                        label="search",
                        total_count=len(search_packages),
                        matched_packages=search_matches,
                        prefix=args.prefix,
                    )
                )

                if search_matches:
                    technical_details.append(
                        build_final_strategy_summary(
                            strategy="search",
                            total_count=len(search_packages),
                            matched_packages=search_matches,
                            prefix=args.prefix,
                        )
                    )
                    return build_response(
                        success=True,
                        message=build_match_message(
                            len(search_matches),
                            args.prefix,
                            "via la recherche WAPT native",
                        ),
                        packages=search_matches,
                        technical_details=technical_details,
                    )

                direct_search_result = run_wapt_command(
                    wapt_get_executable,
                    config_path,
                    ["-F", "0", "-n", "search", args.prefix],
                    timeout_seconds=max(30, args.timeout),
                )
                technical_details.append(
                    format_command_result("search -F 0", direct_search_result)
                )

                direct_search_packages = normalize_packages(
                    extract_package_items(direct_search_result["json_payload"])
                )
                direct_search_matches = filter_packages(direct_search_packages, args.prefix)
                best_total_count = max(best_total_count, len(direct_search_packages))
                technical_details.append(
                    build_package_source_summary(
                        label="search -F 0",
                        total_count=len(direct_search_packages),
                        matched_packages=direct_search_matches,
                        prefix=args.prefix,
                    )
                )

                if direct_search_matches:
                    technical_details.append(
                        build_final_strategy_summary(
                            strategy="search -F 0",
                            total_count=len(direct_search_packages),
                            matched_packages=direct_search_matches,
                            prefix=args.prefix,
                        )
                    )
                    return build_response(
                        success=True,
                        message=build_match_message(
                            len(direct_search_matches),
                            args.prefix,
                            "via la recherche directe WAPT",
                        ),
                        packages=direct_search_matches,
                        technical_details=technical_details,
                    )

                technical_details.append(
                    build_final_strategy_summary(
                        strategy="No matching package source",
                        total_count=best_total_count,
                        matched_packages=[],
                        prefix=args.prefix,
                    )
                )
                if machine_output_requires_refresh:
                    return build_response(
                        success=False,
                        message=(
                            "Le snapshot machine existant ne contient pas encore les package_id techniques requis "
                            "pour appliquer le filtre sur package_id. Regenerer machine_output.json via le helper machine."
                        ),
                        packages=[],
                        technical_details=technical_details,
                    )
                return build_response(
                    success=True,
                    message=build_no_match_message(args.prefix),
                    packages=[],
                    technical_details=technical_details,
                )
    except Exception:
        technical_details.append("Unhandled bridge exception:")
        technical_details.append(traceback.format_exc().strip())
        return build_response(
            success=False,
            message="Le bridge Python WAPT a echoue.",
            packages=[],
            technical_details=technical_details,
        )


def resolve_wapt_get_executable() -> Path:
    candidates = [
        Path(sys.executable).resolve().with_name("wapt-get.exe"),
        Path(r"C:\Program Files (x86)\wapt\wapt-get.exe"),
    ]

    for candidate in candidates:
        if candidate.is_file():
            return candidate

    raise FileNotFoundError("Unable to find wapt-get.exe in the local WAPT installation.")


def build_repo_url(server_url: str) -> str:
    parsed = urlparse(server_url)
    if not parsed.scheme or not parsed.netloc:
        raise ValueError("The provided server URL is invalid.")

    normalized_path = parsed.path.rstrip("/")
    if normalized_path.lower().endswith("/wapt"):
        repo_path = normalized_path
    elif normalized_path:
        repo_path = f"{normalized_path}/wapt"
    else:
        repo_path = "/wapt"

    return urlunparse((parsed.scheme, parsed.netloc, repo_path, "", "", ""))


def build_server_origin(server_url: str) -> str:
    parsed = urlparse(server_url)
    if not parsed.scheme or not parsed.netloc:
        raise ValueError("The provided server URL is invalid.")

    return urlunparse((parsed.scheme, parsed.netloc, "", "", "", ""))


@contextmanager
def temporary_wapt_config(
    args: argparse.Namespace,
    wapt_get_executable: Path,
    repo_url: str,
    client_material: dict | None,
):
    config = configparser.RawConfigParser()
    config.optionxform = str

    base_config_path = wapt_get_executable.with_name("wapt-get.ini")
    if base_config_path.is_file():
        config.read(base_config_path, encoding=get_preferred_text_encoding())

    if not config.has_section("global"):
        config.add_section("global")

    config.set("global", "wapt_server", build_server_origin(args.server_url))
    config.set("global", "repo_url", repo_url)

    if args.ca_cert:
        config.set("global", "verify_cert", args.ca_cert)

    if args.client_pkcs12:
        config.set("global", "personal_certificate_path", args.client_pkcs12)

    if args.password:
        config.set("global", "personal_certificate_password", args.password)

    if client_material is not None:
        config.set("global", "client_certificate", str(client_material["certificate_path"]))
        config.set("global", "client_private_key", str(client_material["private_key_path"]))

    handle, temp_path = tempfile.mkstemp(prefix="wapt-bridge-", suffix=".ini")
    os.close(handle)

    try:
        with open(temp_path, "w", encoding="utf-8") as stream:
            config.write(stream)

        yield Path(temp_path)
    finally:
        try:
            os.remove(temp_path)
        except OSError:
            pass


def run_wapt_command(
    wapt_get_executable: Path,
    config_path: Path,
    command_arguments: list[str],
    timeout_seconds: int,
) -> dict:
    full_command = [str(wapt_get_executable), "-c", str(config_path), "-j", *command_arguments]
    effective_timeout_seconds = max(1, timeout_seconds)
    started_at = time.perf_counter()

    try:
        completed = subprocess.run(
            full_command,
            capture_output=True,
            text=True,
            encoding=get_preferred_text_encoding(),
            errors="replace",
            timeout=effective_timeout_seconds,
            check=False,
        )
    except subprocess.TimeoutExpired as exception:
        duration_seconds = time.perf_counter() - started_at
        return {
            "command": format_command(full_command),
            "ok": False,
            "return_code": None,
            "wapt_exit_code": None,
            "stdout": exception.stdout or "",
            "stderr": exception.stderr or "",
            "json_payload": None,
            "parse_error": None,
            "timed_out": True,
            "timeout_seconds": effective_timeout_seconds,
            "duration_seconds": duration_seconds,
        }

    duration_seconds = time.perf_counter() - started_at

    stdout = (completed.stdout or "").strip()
    stderr = (completed.stderr or "").strip()
    json_payload = None
    parse_error = None

    if stdout:
        try:
            json_payload = json.loads(stdout)
        except json.JSONDecodeError as exception:
            parse_error = str(exception)

    wapt_exit_code = None
    if isinstance(json_payload, dict) and "exit_code" in json_payload:
        try:
            wapt_exit_code = int(json_payload["exit_code"])
        except (TypeError, ValueError):
            wapt_exit_code = None

    ok = completed.returncode == 0 and (wapt_exit_code is None or wapt_exit_code == 0)

    return {
        "command": format_command(full_command),
        "ok": ok,
        "return_code": completed.returncode,
        "wapt_exit_code": wapt_exit_code,
        "stdout": stdout,
        "stderr": stderr,
        "json_payload": json_payload if isinstance(json_payload, dict) else None,
        "parse_error": parse_error,
        "timed_out": False,
        "timeout_seconds": effective_timeout_seconds,
        "duration_seconds": duration_seconds,
    }


def build_local_service_url(endpoint: str) -> str:
    return f"{LOCAL_SERVICE_BASE_URL}/{endpoint.lstrip('/')}"


def resolve_local_service_verify(wapt_get_executable: Path) -> object:
    local_service_certificate = wapt_get_executable.parent / "public" / "localservice.crt"
    return str(local_service_certificate) if local_service_certificate.is_file() else False


def perform_local_service_request(
    endpoint: str,
    timeout_seconds: int,
    verify_option: object,
    method: str = "GET",
    json_body: object | None = None,
    bearer_token: str | None = None,
    basic_username: str | None = None,
) -> dict:
    import requests

    url = build_local_service_url(endpoint)
    headers: dict[str, str] = {}
    if bearer_token:
        headers["Authorization"] = f"Bearer {bearer_token}"

    request_arguments = {
        "method": method,
        "url": url,
        "headers": headers,
        "timeout": max(1, timeout_seconds),
        "verify": verify_option,
        "allow_redirects": True,
    }

    if json_body is not None:
        request_arguments["json"] = json_body

    if basic_username is not None:
        request_arguments["auth"] = (basic_username, "")

    try:
        response = requests.request(**request_arguments)
    except Exception:
        return {
            "url": url,
            "status_code": None,
            "ok": False,
            "json_payload": None,
            "text": "",
            "parse_error": None,
            "error": traceback.format_exc().strip(),
            "response_excerpt": "",
        }

    response_text = (response.text or "").strip()
    json_payload = None
    parse_error = None
    if response_text:
        try:
            json_payload = response.json()
        except ValueError as exception:
            parse_error = str(exception)

    return {
        "url": url,
        "status_code": response.status_code,
        "ok": response.ok,
        "json_payload": json_payload,
        "text": response_text,
        "parse_error": parse_error,
        "error": None,
        "response_excerpt": build_excerpt(json_payload if json_payload is not None else response_text),
    }


def build_package_endpoint_attempt(
    request_result: dict,
    prefix: str,
) -> dict:
    packages = normalize_packages(extract_package_items(request_result["json_payload"]))
    matched_packages = filter_packages(packages, prefix)
    return {
        "url": request_result["url"],
        "status_code": request_result["status_code"],
        "response_excerpt": request_result["response_excerpt"],
        "error": request_result["error"],
        "parse_error": request_result["parse_error"],
        "packages": packages,
        "matched_packages": matched_packages,
        "total_count": len(packages),
        "matched_count": len(matched_packages),
    }


def build_info_endpoint_attempt(request_result: dict) -> dict:
    json_payload = request_result["json_payload"]
    if isinstance(json_payload, list):
        item_count = len(json_payload)
    elif isinstance(json_payload, dict):
        item_count = len(json_payload)
    else:
        item_count = 0

    return {
        "url": request_result["url"],
        "status_code": request_result["status_code"],
        "response_excerpt": request_result["response_excerpt"],
        "error": request_result["error"],
        "parse_error": request_result["parse_error"],
        "item_count": item_count,
    }


def try_local_service_catalog(
    wapt_get_executable: Path,
    timeout_seconds: int,
    prefix: str,
) -> dict:
    verify_option = resolve_local_service_verify(wapt_get_executable)
    ping_result = perform_local_service_request(
        endpoint=LOCAL_SERVICE_PING_ENDPOINT,
        timeout_seconds=timeout_seconds,
        verify_option=verify_option,
    )

    attempts: list[dict] = []
    requires_bearer = False
    for endpoint in LOCAL_SERVICE_CATALOG_ENDPOINTS:
        request_result = perform_local_service_request(
            endpoint=endpoint,
            timeout_seconds=timeout_seconds,
            verify_option=verify_option,
        )
        attempt = build_package_endpoint_attempt(request_result, prefix)
        attempts.append(attempt)
        if request_result["status_code"] in (401, 403):
            requires_bearer = True
        if attempt["total_count"] > 0:
            return {
                "strategy": "LocalServiceCatalog",
                "ok": True,
                "requires_bearer": requires_bearer,
                "verify_option": verify_option,
                "ping": ping_result,
                "attempts": attempts,
                "packages": attempt["packages"],
                "matched_packages": attempt["matched_packages"],
                "total_count": attempt["total_count"],
                "matched_count": attempt["matched_count"],
                "selected_endpoint": attempt["url"],
                "max_total_count": attempt["total_count"],
            }

    return {
        "strategy": "LocalServiceCatalog",
        "ok": False,
        "requires_bearer": requires_bearer,
        "verify_option": verify_option,
        "ping": ping_result,
        "attempts": attempts,
        "packages": [],
        "matched_packages": [],
        "total_count": 0,
        "matched_count": 0,
        "selected_endpoint": None,
        "max_total_count": max((attempt["total_count"] for attempt in attempts), default=0),
    }


def try_local_service_bearer(
    wapt_get_executable: Path,
    timeout_seconds: int,
    prefix: str,
) -> dict:
    verify_option = resolve_local_service_verify(wapt_get_executable)
    identity_attempts: list[dict] = []
    max_total_count = 0

    for identity_label, username in build_local_service_identity_candidates():
        identity_result = try_local_service_identity(
            identity_label=identity_label,
            username=username,
            timeout_seconds=timeout_seconds,
            verify_option=verify_option,
            prefix=prefix,
        )
        identity_attempts.append(identity_result)
        max_total_count = max(max_total_count, identity_result["max_total_count"])
        if identity_result["total_count"] > 0:
            return {
                "strategy": "LocalServiceBearer",
                "ok": True,
                "verify_option": verify_option,
                "identity_attempts": identity_attempts,
                "packages": identity_result["packages"],
                "matched_packages": identity_result["matched_packages"],
                "total_count": identity_result["total_count"],
                "matched_count": identity_result["matched_count"],
                "selected_endpoint": identity_result["selected_endpoint"],
                "max_total_count": max_total_count,
            }

    return {
        "strategy": "LocalServiceBearer",
        "ok": False,
        "verify_option": verify_option,
        "identity_attempts": identity_attempts,
        "packages": [],
        "matched_packages": [],
        "total_count": 0,
        "matched_count": 0,
        "selected_endpoint": None,
        "max_total_count": max_total_count,
    }


def build_local_service_identity_candidates() -> list[tuple[str, str]]:
    candidates: list[tuple[str, str]] = []
    machine_name = (os.environ.get("COMPUTERNAME") or "").strip()
    if machine_name:
        candidates.append(("machine", f"{machine_name}$"))

    current_user = (os.environ.get("USERNAME") or getpass.getuser() or "").strip()
    if current_user and all(current_user.lower() != username.lower() for _, username in candidates):
        candidates.append(("current-user", current_user))

    return candidates


def try_local_service_identity(
    identity_label: str,
    username: str,
    timeout_seconds: int,
    verify_option: object,
    prefix: str,
) -> dict:
    client_secret = secrets.token_hex(32)
    login_result = perform_local_service_request(
        endpoint="/login",
        timeout_seconds=timeout_seconds,
        verify_option=verify_option,
        method="POST",
        json_body={"secret": client_secret},
        basic_username=username,
    )

    login_payload = login_result["json_payload"] if isinstance(login_result["json_payload"], dict) else {}
    token_result = obtain_local_service_bearer_token(login_payload, client_secret)

    catalog_attempts: list[dict] = []
    info_attempts: list[dict] = []
    packages: list[dict] = []
    matched_packages: list[dict] = []
    selected_endpoint = None

    bearer_token = token_result["token"]
    if bearer_token:
        for endpoint in LOCAL_SERVICE_CATALOG_ENDPOINTS:
            request_result = perform_local_service_request(
                endpoint=endpoint,
                timeout_seconds=timeout_seconds,
                verify_option=verify_option,
                bearer_token=bearer_token,
            )
            attempt = build_package_endpoint_attempt(request_result, prefix)
            catalog_attempts.append(attempt)
            if attempt["total_count"] > 0:
                packages = attempt["packages"]
                matched_packages = attempt["matched_packages"]
                selected_endpoint = attempt["url"]
                break

        for endpoint in LOCAL_SERVICE_INFO_ENDPOINTS:
            request_result = perform_local_service_request(
                endpoint=endpoint,
                timeout_seconds=timeout_seconds,
                verify_option=verify_option,
                bearer_token=bearer_token,
            )
            info_attempts.append(build_info_endpoint_attempt(request_result))

    return {
        "identity_label": identity_label,
        "username": username,
        "login_url": login_result["url"],
        "login_status_code": login_result["status_code"],
        "login_excerpt": login_result["response_excerpt"],
        "groups": normalize_groups(login_payload.get("groups")),
        "token_mode": token_result["mode"],
        "token_source": token_result["source"],
        "token_accessible": token_result["accessible"],
        "token_acquisition": token_result["acquisition"],
        "token_error": token_result["error"],
        "catalog_attempts": catalog_attempts,
        "info_attempts": info_attempts,
        "packages": packages,
        "matched_packages": matched_packages,
        "total_count": len(packages),
        "matched_count": len(matched_packages),
        "selected_endpoint": selected_endpoint,
        "max_total_count": max((attempt["total_count"] for attempt in catalog_attempts), default=0),
    }


def normalize_groups(raw_groups: object) -> list[str]:
    if not isinstance(raw_groups, list):
        return []
    return [str(group).strip() for group in raw_groups if str(group).strip()]


def obtain_local_service_bearer_token(login_payload: dict, client_secret: str) -> dict:
    token_result = {
        "mode": None,
        "source": None,
        "accessible": False,
        "acquisition": "No bearer token information was returned by /login.",
        "token": None,
        "error": None,
    }

    if not isinstance(login_payload, dict):
        return token_result

    try:
        if login_payload.get("token"):
            encrypted_token = base64.b64decode(str(login_payload["token"]))
            token_result["mode"] = "embedded_token"
            token_result["source"] = "login-response"
            token_result["accessible"] = True
            token_result["token"] = decrypt_local_service_token(encrypted_token, client_secret)
            token_result["acquisition"] = (
                "Encrypted token returned inline by /login, base64-decoded, decrypted with the same client secret, then sent as Authorization: Bearer <token>."
            )
            return token_result

        token_filepath = str(login_payload.get("token_filepath") or "").strip()
        if token_filepath:
            token_result["mode"] = "token_filepath"
            token_result["source"] = token_filepath
            encrypted_token, read_error = read_binary_file(token_filepath)
            if encrypted_token is None:
                token_result["error"] = read_error
                token_result["acquisition"] = (
                    "Encrypted token filepath returned by /login, but the current bridge process cannot read that file."
                )
                return token_result

            token_result["accessible"] = True
            token_result["token"] = decrypt_local_service_token(encrypted_token, client_secret)
            token_result["acquisition"] = (
                "Encrypted token filepath returned by /login, read by the bridge, decrypted with the same client secret, then sent as Authorization: Bearer <token>."
            )
            try_delete_file(token_filepath)
            return token_result
    except Exception:
        token_result["error"] = traceback.format_exc().strip()
        token_result["acquisition"] = "Token acquisition failed while decoding the local service login response."

    return token_result


def decrypt_local_service_token(encrypted_token: bytes, client_secret: str) -> str:
    import waptlicences

    decrypted_token = waptlicences.decrypt_aes_pkcs7(encrypted_token, client_secret)
    if isinstance(decrypted_token, bytes):
        return decrypted_token.decode("utf-8").strip()
    return str(decrypted_token).strip()


def read_binary_file(file_path: str) -> tuple[bytes | None, str | None]:
    try:
        return Path(file_path).read_bytes(), None
    except Exception as exception:
        return None, str(exception)


def try_delete_file(file_path: str) -> None:
    try:
        Path(file_path).unlink()
    except OSError:
        pass


def get_local_service_machine_context_paths() -> dict:
    script_path = Path(__file__).resolve()
    bridge_directory = LOCAL_SERVICE_MACHINE_BRIDGE_DIR
    return {
        "bridge_directory": bridge_directory,
        "request_path": bridge_directory / "machine_request.json",
        "output_path": bridge_directory / "machine_output.json",
        "helper_script_path": script_path.with_name("wapt_local_service_machine_helper.py"),
        "installer_script_path": script_path.with_name("install_wapt_machine_context_task.ps1"),
    }


def try_load_json_file(file_path: Path) -> tuple[dict | None, str | None]:
    try:
        raw_text = file_path.read_text(encoding="utf-8-sig")
    except Exception as exception:
        return None, str(exception)

    try:
        json_payload = json.loads(raw_text)
    except json.JSONDecodeError as exception:
        return None, str(exception)

    if isinstance(json_payload, dict):
        return json_payload, None

    return None, "JSON payload is not an object."


def build_machine_output_snapshot(
    output_path: Path,
    expected_request_id: str | None,
    max_age_seconds: int,
) -> dict:
    snapshot = {
        "exists": output_path.is_file(),
        "size": 0,
        "last_write_time": None,
        "age_seconds": None,
        "recent": False,
        "request_id": None,
        "expected_request_id": expected_request_id,
        "request_id_matches": False,
        "json_payload": None,
        "parse_error": None,
        "error": None,
        "contains_technical_package_ids": False,
        "usable": False,
    }

    if not snapshot["exists"]:
        return snapshot

    try:
        stat_result = output_path.stat()
    except OSError as exception:
        snapshot["error"] = str(exception)
        return snapshot

    snapshot["size"] = stat_result.st_size
    last_write_timestamp = stat_result.st_mtime
    snapshot["last_write_time"] = datetime.datetime.fromtimestamp(last_write_timestamp).isoformat()
    snapshot["age_seconds"] = max(0.0, time.time() - last_write_timestamp)
    snapshot["recent"] = snapshot["age_seconds"] <= max(1, max_age_seconds)

    json_payload, parse_error = try_load_json_file(output_path)
    snapshot["json_payload"] = json_payload
    snapshot["parse_error"] = parse_error
    if json_payload is None:
        return snapshot

    request_id = str(json_payload.get("request_id") or "").strip() or None
    snapshot["request_id"] = request_id
    snapshot["request_id_matches"] = expected_request_id is not None and request_id == expected_request_id
    raw_packages = json_payload.get("packages")
    if isinstance(raw_packages, list):
        snapshot["contains_technical_package_ids"] = any(
            isinstance(item, dict) and first_non_empty(item, "package_id", "package")
            for item in raw_packages
        )

    snapshot["usable"] = bool(json_payload.get("success")) and snapshot["recent"] and (
        expected_request_id is None or snapshot["request_id_matches"]
    )
    return snapshot


def load_machine_output_snapshot(
    output_path: Path,
    expected_request_id: str | None,
    max_age_seconds: int,
) -> dict:
    return build_machine_output_snapshot(
        output_path=output_path,
        expected_request_id=expected_request_id,
        max_age_seconds=max_age_seconds,
    )


def try_load_existing_machine_context_output(
    request_path: Path,
    output_path: Path,
    prefix: str,
    max_age_seconds: int,
) -> tuple[dict | None, dict | None]:
    if not request_path.is_file() or not output_path.is_file():
        return None, None

    request_payload, request_error = try_load_json_file(request_path)
    if request_payload is None:
        return None, {
            "request_error": request_error,
            "request_payload": None,
            "output_snapshot": load_machine_output_snapshot(
                output_path=output_path,
                expected_request_id=None,
                max_age_seconds=max_age_seconds,
            ),
        }

    request_id = str(request_payload.get("request_id") or "").strip() or None
    request_prefix = str(request_payload.get("prefix") or "").strip()
    output_snapshot = load_machine_output_snapshot(
        output_path=output_path,
        expected_request_id=request_id,
        max_age_seconds=max_age_seconds,
    )
    existing_output_prefix = str((output_snapshot.get("json_payload") or {}).get("prefix") or "").strip()
    prefix_matches = request_prefix == prefix or existing_output_prefix == prefix
    # Explicit reuse mode keeps the last successful machine-context snapshot even when
    # the current process cannot refresh the scheduled-task output on demand.
    output_is_reusable = bool((output_snapshot.get("json_payload") or {}).get("success"))
    if not prefix_matches or not output_is_reusable:
        return None, {
            "request_error": None,
            "request_payload": request_payload,
            "output_snapshot": output_snapshot,
        }

    return output_snapshot["json_payload"], {
        "request_error": None,
        "request_payload": request_payload,
        "output_snapshot": output_snapshot,
    }


def run_external_command(command: list[str], timeout_seconds: int) -> dict:
    normalized_command = [str(argument) for argument in command]
    try:
        completed = subprocess.run(
            normalized_command,
            capture_output=True,
            text=True,
            encoding=get_preferred_text_encoding(),
            errors="replace",
            timeout=max(1, timeout_seconds),
            check=False,
        )
    except subprocess.TimeoutExpired as exception:
        return {
            "command": format_command(normalized_command),
            "ok": False,
            "return_code": None,
            "stdout": (exception.stdout or "").strip(),
            "stderr": (exception.stderr or "").strip(),
            "timed_out": True,
        }
    except Exception:
        return {
            "command": format_command(normalized_command),
            "ok": False,
            "return_code": None,
            "stdout": "",
            "stderr": traceback.format_exc().strip(),
            "timed_out": False,
        }

    stdout = (completed.stdout or "").strip()
    stderr = (completed.stderr or "").strip()
    return {
        "command": format_command(normalized_command),
        "ok": completed.returncode == 0,
        "return_code": completed.returncode,
        "stdout": stdout,
        "stderr": stderr,
        "timed_out": False,
    }


def command_output_contains_access_denied(command_result: dict | None) -> bool:
    if not command_result:
        return False

    combined_output = "\n".join(
        value for value in (command_result.get("stdout"), command_result.get("stderr")) if value
    ).lower()
    return "acc" in combined_output and "refus" in combined_output or "access is denied" in combined_output


def wait_for_machine_context_output(
    output_path: Path,
    request_id: str,
    timeout_seconds: int,
    max_age_seconds: int,
) -> dict:
    deadline = time.monotonic() + max(1, timeout_seconds)
    last_snapshot = load_machine_output_snapshot(
        output_path=output_path,
        expected_request_id=request_id,
        max_age_seconds=max_age_seconds,
    )

    while time.monotonic() < deadline:
        snapshot = load_machine_output_snapshot(
            output_path=output_path,
            expected_request_id=request_id,
            max_age_seconds=max_age_seconds,
        )
        last_snapshot = snapshot
        if snapshot["usable"]:
            return {
                "ok": True,
                "timed_out": False,
                "json_payload": snapshot["json_payload"],
                "parse_error": snapshot["parse_error"],
                "error": snapshot["error"],
                "output_snapshot": snapshot,
            }

        time.sleep(1)

    return {
        "ok": False,
        "timed_out": True,
        "json_payload": None,
        "parse_error": last_snapshot["parse_error"],
        "error": last_snapshot["error"],
        "output_snapshot": last_snapshot,
    }


def try_local_service_machine_context(
    wapt_get_executable: Path,
    timeout_seconds: int,
    prefix: str,
    use_existing_output: bool,
) -> dict:
    verify_option = resolve_local_service_verify(wapt_get_executable)
    ping_result = perform_local_service_request(
        endpoint=LOCAL_SERVICE_PING_ENDPOINT,
        timeout_seconds=timeout_seconds,
        verify_option=verify_option,
    )

    paths = get_local_service_machine_context_paths()
    request_path = paths["request_path"]
    output_path = paths["output_path"]
    request_path.parent.mkdir(parents=True, exist_ok=True)

    max_output_age_seconds = max(60, timeout_seconds * 3, LOCAL_SERVICE_MACHINE_OUTPUT_MAX_AGE_SECONDS)
    existing_output = None
    existing_output_details = None
    if use_existing_output:
        existing_output, existing_output_details = try_load_existing_machine_context_output(
            request_path=request_path,
            output_path=output_path,
            prefix=prefix,
            max_age_seconds=max_output_age_seconds,
        )

    request_id = None
    output_snapshot = None
    if existing_output is None:
        request_id = uuid.uuid4().hex
        request_payload = {
            "request_id": request_id,
            "prefix": prefix,
            "timeout_seconds": max(1, timeout_seconds),
        }
        request_path.write_text(json.dumps(request_payload, ensure_ascii=True), encoding="utf-8")
        output_snapshot = load_machine_output_snapshot(
            output_path=output_path,
            expected_request_id=request_id,
            max_age_seconds=max_output_age_seconds,
        )
    else:
        request_id = str(existing_output.get("request_id") or "").strip() or None
        output_snapshot = existing_output_details["output_snapshot"] if existing_output_details else None

    task_query_result = run_external_command(
        ["schtasks", "/Query", "/TN", LOCAL_SERVICE_MACHINE_TASK_NAME],
        timeout_seconds=30,
    )

    task_install_result = None
    if not task_query_result["ok"] and paths["installer_script_path"].is_file():
        wapt_python = wapt_get_executable.with_name("waptpython.exe")
        task_install_result = run_external_command(
            [
                "powershell.exe",
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                str(paths["installer_script_path"]),
                "-TaskName",
                LOCAL_SERVICE_MACHINE_TASK_NAME,
                "-PythonExecutablePath",
                str(wapt_python),
                "-HelperScriptPath",
                str(paths["helper_script_path"]),
                "-RequestPath",
                str(request_path),
                "-OutputPath",
                str(output_path),
            ],
            timeout_seconds=120,
        )
        if task_install_result["ok"]:
            task_query_result = run_external_command(
                ["schtasks", "/Query", "/TN", LOCAL_SERVICE_MACHINE_TASK_NAME],
                timeout_seconds=30,
            )

    task_run_result = None
    helper_result = {
        "ok": existing_output is not None,
        "timed_out": False,
        "json_payload": existing_output,
        "parse_error": output_snapshot["parse_error"] if output_snapshot else None,
        "error": output_snapshot["error"] if output_snapshot else None,
        "output_snapshot": output_snapshot,
    }

    query_access_denied = command_output_contains_access_denied(task_query_result)
    can_attempt_task_run = existing_output is None and (task_query_result["ok"] or query_access_denied)

    if can_attempt_task_run:
        task_run_result = run_external_command(
            ["schtasks", "/Run", "/TN", LOCAL_SERVICE_MACHINE_TASK_NAME],
            timeout_seconds=30,
        )
        if task_run_result["ok"]:
            helper_result = wait_for_machine_context_output(
                output_path=output_path,
                request_id=request_id,
                timeout_seconds=max(30, timeout_seconds + 15),
                max_age_seconds=max_output_age_seconds,
            )

    if existing_output is None and not helper_result["ok"]:
        helper_result["output_snapshot"] = load_machine_output_snapshot(
            output_path=output_path,
            expected_request_id=request_id,
            max_age_seconds=max_output_age_seconds,
        )

    helper_payload = (
        helper_result["json_payload"]
        if helper_result["ok"] and isinstance(helper_result["json_payload"], dict)
        else {}
    )
    packages = normalize_packages(helper_payload.get("packages"))
    matched_packages = filter_packages(packages, prefix)
    total_count = helper_payload.get("total_count")
    if not isinstance(total_count, int):
        total_count = len(packages)
    matched_count = helper_payload.get("matched_count")
    if not isinstance(matched_count, int):
        matched_count = len(matched_packages)
    output_snapshot_for_validation = helper_result.get("output_snapshot") or output_snapshot or {}
    contains_technical_package_ids = bool(output_snapshot_for_validation.get("contains_technical_package_ids"))
    legacy_output_requires_refresh = (
        bool(helper_payload.get("success"))
        and total_count > 0
        and len(packages) == 0
        and not contains_technical_package_ids
    )

    return {
        "strategy": "LocalServiceMachineContext",
        "ok": bool(helper_payload.get("success")) and total_count > 0 and not legacy_output_requires_refresh,
        "verify_option": verify_option,
        "ping": ping_result,
        "task_name": LOCAL_SERVICE_MACHINE_TASK_NAME,
        "bridge_directory": str(paths["bridge_directory"]),
        "request_path": str(request_path),
        "output_path": str(output_path),
        "helper_script_path": str(paths["helper_script_path"]),
        "installer_script_path": str(paths["installer_script_path"]),
        "task_query_result": task_query_result,
        "task_install_result": task_install_result,
        "task_run_result": task_run_result,
        "query_access_denied": query_access_denied,
        "use_existing_output": use_existing_output,
        "used_existing_output": existing_output is not None,
        "existing_output_details": existing_output_details,
        "helper_result": helper_result,
        "helper_message": str(helper_payload.get("message") or "").strip(),
        "helper_technical_details": str(helper_payload.get("technical_details") or "").strip(),
        "context_used": helper_payload.get("context_used") if isinstance(helper_payload.get("context_used"), dict) else None,
        "packages": packages,
        "matched_packages": matched_packages,
        "total_count": total_count,
        "matched_count": matched_count,
        "legacy_output_requires_refresh": legacy_output_requires_refresh,
        "contains_technical_package_ids": contains_technical_package_ids,
        "selected_endpoint": str(helper_payload.get("selected_endpoint") or "").strip() or None,
        "max_total_count": total_count,
    }


def format_external_command_result(label: str, command_result: dict | None) -> list[str]:
    if command_result is None:
        return [f"{label}: <not run>"]

    lines = [f"{label}: {command_result['command']}"]
    if command_result["timed_out"]:
        lines.append("Timed out: True")
    else:
        lines.append(f"Process return code: {command_result['return_code']}")
    lines.append(f"Succeeded: {command_result['ok']}")
    if command_result["stdout"]:
        lines.append(f"Stdout excerpt: {build_excerpt(command_result['stdout'])}")
    if command_result["stderr"]:
        lines.append(f"Stderr: {build_excerpt(command_result['stderr'])}")
    return lines


def format_local_service_machine_context_result(result: dict, prefix: str) -> str:
    lines = [
        "Strategy [LocalServiceMachineContext]: scheduled helper under machine/service context",
        f"Base URL: {LOCAL_SERVICE_BASE_URL}",
        f"TLS verify: {result['verify_option'] if result['verify_option'] else '<disabled>'}",
        f"Scheduled task: {result['task_name']}",
        f"Bridge directory: {result['bridge_directory']}",
        f"Request path: {result['request_path']}",
        f"Output path: {result['output_path']}",
        f"Helper script: {result['helper_script_path']}",
        f"Installer script: {result['installer_script_path']}",
        f"Task query access denied: {result['query_access_denied']}",
        f"Use existing machine output: {result['use_existing_output']}",
        f"Used existing machine output: {result['used_existing_output']}",
    ]

    ping_result = result["ping"]
    lines.extend(
        [
            f"Probe endpoint: {ping_result['url']}",
            f"Probe status code: {ping_result['status_code']}",
            f"Probe response excerpt: {ping_result['response_excerpt'] or '<empty>'}",
        ]
    )

    lines.extend(format_external_command_result("Task query", result["task_query_result"]))
    lines.extend(format_external_command_result("Task install", result["task_install_result"]))
    lines.extend(format_external_command_result("Task run", result["task_run_result"]))

    existing_output_details = result["existing_output_details"] or {}
    existing_request_payload = existing_output_details.get("request_payload") or {}
    if existing_output_details:
        lines.append("Existing output probe:")
        if existing_output_details.get("request_error"):
            lines.append(f"Existing request error: {build_excerpt(existing_output_details['request_error'])}")
        if existing_request_payload:
            lines.append(
                f"Existing request prefix: {str(existing_request_payload.get('prefix') or '').strip() or '<none>'}"
            )
            lines.append(
                f"Existing request id: {str(existing_request_payload.get('request_id') or '').strip() or '<none>'}"
            )
        lines.extend(format_machine_output_snapshot(existing_output_details.get("output_snapshot")))

    helper_result = result["helper_result"]
    lines.append(f"Helper output available: {helper_result['ok']}")
    lines.append(f"Helper output timed out: {helper_result['timed_out']}")
    lines.extend(format_machine_output_snapshot(helper_result.get("output_snapshot")))
    if helper_result["parse_error"]:
        lines.append(f"Helper output parse error: {helper_result['parse_error']}")
    if helper_result["error"]:
        lines.append(f"Helper output error: {build_excerpt(helper_result['error'])}")

    context_used = result["context_used"] or {}
    if context_used:
        lines.append(
            f"Helper execution identity: {context_used.get('username') or '<unknown>'}"
        )
        lines.append(
            f"Helper user domain: {context_used.get('userdomain') or '<unknown>'}"
        )

    lines.append(f"Helper message: {result['helper_message'] or '<none>'}")
    lines.append(f"Selected endpoint: {result['selected_endpoint'] or '<none>'}")
    lines.append(f"Machine output contains technical package ids: {result['contains_technical_package_ids']}")
    lines.append(f"Legacy machine output requires refresh: {result['legacy_output_requires_refresh']}")
    lines.extend(build_filter_summary_lines(result["total_count"], result["matched_packages"], prefix))

    if result["helper_technical_details"]:
        lines.append(
            f"Helper technical details: {build_excerpt(result['helper_technical_details'], max_length=1500)}"
        )

    return "\n".join(lines)


def format_machine_output_snapshot(snapshot: dict | None) -> list[str]:
    if not snapshot:
        return ["Output file exists: False"]

    return [
        f"Output file exists: {snapshot['exists']}",
        f"Output file size: {snapshot['size']}",
        f"Output file last write time: {snapshot['last_write_time'] or '<none>'}",
        f"Output file age seconds: {snapshot['age_seconds'] if snapshot['age_seconds'] is not None else '<none>'}",
        f"Output file is recent: {snapshot['recent']}",
        f"Output file contains technical package ids: {snapshot['contains_technical_package_ids']}",
        f"Output file prefix: {str((snapshot['json_payload'] or {}).get('prefix') or '').strip() or '<none>'}",
        f"Request id lu: {snapshot['request_id'] or '<none>'}",
        f"Request id attendu: {snapshot['expected_request_id'] or '<none>'}",
        f"Request id match: {snapshot['request_id_matches']}",
        f"Output file usable: {snapshot['usable']}",
    ]


def format_local_service_catalog_result(result: dict, prefix: str) -> str:
    lines = [
        "Strategy [LocalServiceCatalog]: local WAPT service catalog probe",
        f"Base URL: {LOCAL_SERVICE_BASE_URL}",
        f"TLS verify: {result['verify_option'] if result['verify_option'] else '<disabled>'}",
        f"Bearer required: {result['requires_bearer']}",
    ]

    ping_result = result["ping"]
    lines.extend(
        [
            f"Probe endpoint: {ping_result['url']}",
            f"Probe status code: {ping_result['status_code']}",
            f"Probe response excerpt: {ping_result['response_excerpt'] or '<empty>'}",
        ]
    )

    for attempt in result["attempts"]:
        lines.extend(
            [
                f"Endpoint URL: {attempt['url']}",
                f"Status code: {attempt['status_code']}",
                f"Response excerpt: {attempt['response_excerpt'] or '<empty>'}",
            ]
        )
        lines.extend(build_filter_summary_lines(attempt["total_count"], attempt["matched_packages"], prefix))
        if attempt["parse_error"]:
            lines.append(f"JSON parse error: {attempt['parse_error']}")
        if attempt["error"]:
            lines.append(f"Error: {build_excerpt(attempt['error'])}")

    return "\n".join(lines)


def format_local_service_bearer_result(result: dict, prefix: str) -> str:
    lines = [
        "Strategy [LocalServiceBearer]: local WAPT service with explicit bearer/token handling",
        f"Base URL: {LOCAL_SERVICE_BASE_URL}",
        f"TLS verify: {result['verify_option'] if result['verify_option'] else '<disabled>'}",
    ]

    for identity_result in result["identity_attempts"]:
        lines.extend(
            [
                f"Identity: {identity_result['identity_label']} ({identity_result['username']})",
                f"Login endpoint: {identity_result['login_url']}",
                f"Login status code: {identity_result['login_status_code']}",
                f"Login groups: {', '.join(identity_result['groups']) if identity_result['groups'] else '<none>'}",
                f"Login response excerpt: {identity_result['login_excerpt'] or '<empty>'}",
                f"Token mode: {identity_result['token_mode'] or '<none>'}",
                f"Token source: {identity_result['token_source'] or '<none>'}",
                f"Token accessible: {identity_result['token_accessible']}",
                f"Token acquisition: {identity_result['token_acquisition']}",
            ]
        )
        if identity_result["token_error"]:
            lines.append(f"Token error: {build_excerpt(identity_result['token_error'])}")

        for attempt in identity_result["catalog_attempts"]:
            lines.extend(
                [
                    f"Catalog endpoint URL: {attempt['url']}",
                    f"Catalog status code: {attempt['status_code']}",
                    f"Response excerpt: {attempt['response_excerpt'] or '<empty>'}",
                ]
            )
            lines.extend(build_filter_summary_lines(attempt["total_count"], attempt["matched_packages"], prefix))
            if attempt["parse_error"]:
                lines.append(f"JSON parse error: {attempt['parse_error']}")
            if attempt["error"]:
                lines.append(f"Error: {build_excerpt(attempt['error'])}")

        for info_attempt in identity_result["info_attempts"]:
            lines.extend(
                [
                    f"Info endpoint URL: {info_attempt['url']}",
                    f"Info status code: {info_attempt['status_code']}",
                    f"Info item count: {info_attempt['item_count']}",
                    f"Info response excerpt: {info_attempt['response_excerpt'] or '<empty>'}",
                ]
            )
            if info_attempt["parse_error"]:
                lines.append(f"JSON parse error: {info_attempt['parse_error']}")
            if info_attempt["error"]:
                lines.append(f"Error: {build_excerpt(info_attempt['error'])}")

    return "\n".join(lines)


def prepare_client_certificate_material(
    args: argparse.Namespace,
    stack: ExitStack,
) -> dict | None:
    client_cert = (args.client_cert or "").strip()
    client_key = (args.client_key or "").strip()

    if client_cert:
        return {
            "source": "PEM",
            "certificate_path": Path(client_cert),
            "private_key_path": Path(client_key or client_cert),
        }

    client_pkcs12 = (args.client_pkcs12 or "").strip()
    if not client_pkcs12:
        return None

    from waptcrypto import SSLPKCS12

    temp_dir = Path(stack.enter_context(tempfile.TemporaryDirectory(prefix="wapt-bridge-cert-")))
    client_p12 = SSLPKCS12(
        client_pkcs12,
        password=args.password.encode("utf-8") if args.password else None,
    )

    certificate_path = temp_dir / "client-chain.crt"
    private_key_path = temp_dir / "client.pem"

    certificate_bytes = client_p12.certificate.as_pem()
    if client_p12.ca_certificates:
        certificate_bytes += b"".join(
            certificate.as_pem() for certificate in client_p12.ca_certificates
        )

    certificate_path.write_bytes(certificate_bytes)
    client_p12.private_key.save_as_pem(str(private_key_path))

    return {
        "source": "PKCS12",
        "certificate_path": certificate_path,
        "private_key_path": private_key_path,
    }


def resolve_repo_verify_cert(args: argparse.Namespace, wapt_get_executable: Path) -> object:
    if args.ca_cert:
        return args.ca_cert

    base_config_path = wapt_get_executable.with_name("wapt-get.ini")
    if not base_config_path.is_file():
        return True

    config = configparser.RawConfigParser()
    config.optionxform = str
    config.read(base_config_path, encoding=get_preferred_text_encoding())

    if config.has_option("global", "verify_cert"):
        value = config.get("global", "verify_cert").strip()
        if value:
            return value

    return True


def try_load_packages_from_wapt_repo(
    repo_url: str,
    verify_cert: object,
    timeout_seconds: int,
    client_material: dict | None,
) -> dict:
    endpoint_url = f"{repo_url.rstrip('/')}/Packages"
    if client_material is None:
        return {
            "ok": False,
            "source": None,
            "packages": [],
            "discarded_count": 0,
            "endpoint_url": endpoint_url,
            "status_code": None,
            "error": "No client certificate material available for direct repository access.",
        }

    try:
        from waptpackage import WaptRemoteRepo

        repository = WaptRemoteRepo(
            url=repo_url,
            verify_cert=verify_cert,
            timeout=max(1, timeout_seconds),
        )
        repository.client_certificate = str(client_material["certificate_path"])
        repository.client_private_key = str(client_material["private_key_path"])

        load_result = repository._load_packages_index()
        packages = [package_entry_to_package_dict(package) for package in repository.packages()]

        return {
            "ok": True,
            "source": client_material["source"],
            "packages": packages,
            "discarded_count": len(load_result.get("discarded") or []),
            "endpoint_url": endpoint_url,
            "status_code": 200,
            "error": None,
        }
    except Exception as exception:
        response = getattr(exception, "response", None)
        return {
            "ok": False,
            "source": client_material["source"],
            "packages": [],
            "discarded_count": 0,
            "endpoint_url": endpoint_url,
            "status_code": getattr(response, "status_code", None),
            "error": traceback.format_exc().strip(),
        }


def package_entry_to_package_dict(package_entry: object) -> dict:
    return {
        "package_id": str(getattr(package_entry, "package", "") or "").strip(),
        "name": str(getattr(package_entry, "name", "") or "").strip(),
        "version": str(getattr(package_entry, "version", "") or "").strip(),
        "description": str(
            getattr(package_entry, "description", "")
            or getattr(package_entry, "locale_description", "")
            or getattr(package_entry, "summary", "")
            or ""
        ).strip(),
        "architecture": str(
            getattr(package_entry, "architecture", "")
            or getattr(package_entry, "arch", "")
            or ""
        ).strip(),
        "maturity": str(getattr(package_entry, "maturity", "") or "").strip(),
    }


def extract_package_items(raw_payload: object) -> list[dict]:
    if isinstance(raw_payload, list):
        direct_packages = [
            item
            for item in raw_payload
            if isinstance(item, dict) and looks_like_package_dict(item)
        ]
        if direct_packages:
            return direct_packages

        extracted: list[dict] = []
        for item in raw_payload:
            extracted.extend(extract_package_items(item))
            if extracted:
                return extracted
        return extracted

    if isinstance(raw_payload, dict):
        if looks_like_package_dict(raw_payload):
            return [raw_payload]

        for key in ("result", "packages", "items", "rows", "data"):
            extracted = extract_package_items(raw_payload.get(key))
            if extracted:
                return extracted

        for value in raw_payload.values():
            extracted = extract_package_items(value)
            if extracted:
                return extracted

    return []


def looks_like_package_dict(item: dict) -> bool:
    return bool(first_non_empty(item, "package_id", "package"))


def extract_update_package_count(json_payload: object) -> int | None:
    if not isinstance(json_payload, dict):
        return None

    result = json_payload.get("result")
    if isinstance(result, dict):
        count = result.get("count")
        if isinstance(count, int):
            return count

    tasks = json_payload.get("tasks")
    if isinstance(tasks, list):
        for task in tasks:
            if not isinstance(task, dict):
                continue
            task_result = task.get("result")
            if isinstance(task_result, dict) and isinstance(task_result.get("count"), int):
                return int(task_result["count"])

    return None


def normalize_packages(raw_packages: object) -> list[dict]:
    if not isinstance(raw_packages, list):
        return []

    packages: list[dict] = []
    for item in raw_packages:
        if not isinstance(item, dict):
            continue

        package_id = first_non_empty(item, "package_id", "package")
        name = first_non_empty(item, "name")
        if not package_id:
            continue

        packages.append(
            {
                "package_id": package_id,
                "name": name,
                "version": first_non_empty(item, "version"),
                "description": first_non_empty(item, "description", "locale_description", "summary"),
                "architecture": first_non_empty(item, "architecture", "arch"),
                "maturity": first_non_empty(item, "maturity"),
            }
        )

    return packages


def filter_packages(packages: list[dict], prefix: str) -> list[dict]:
    normalized_prefix = (prefix or "").strip().lower()
    if not normalized_prefix:
        return []

    filtered = [
        package
        for package in packages
        if normalized_prefix in package["package_id"].lower()
    ]
    filtered.sort(key=lambda package: (package["package_id"], package["version"], package["name"]))
    return filtered


def first_non_empty(item: dict, *keys: str) -> str:
    for key in keys:
        value = item.get(key)
        if value is None:
            continue
        value_as_string = str(value).strip()
        if value_as_string:
            return value_as_string
    return ""


def format_command(command: list[str]) -> str:
    return " ".join(shlex.quote(argument) for argument in command)


def format_command_result(label: str, command_result: dict) -> str:
    lines = [f"Command [{label}]: {command_result['command']}"]

    timeout_seconds = command_result.get("timeout_seconds")
    if timeout_seconds is not None:
        lines.append(f"Timeout used: {timeout_seconds} second(s)")

    duration_seconds = command_result.get("duration_seconds")
    if duration_seconds is not None:
        lines.append(f"Elapsed duration: {duration_seconds:.2f} second(s)")

    if command_result["timed_out"]:
        lines.append("Timed out: True")
    else:
        lines.append(f"Process return code: {command_result['return_code']}")

    if command_result["wapt_exit_code"] is not None:
        lines.append(f"WAPT exit code: {command_result['wapt_exit_code']}")

    lines.append(f"Succeeded: {command_result['ok']}")

    json_payload = command_result["json_payload"]
    if isinstance(json_payload, dict):
        if "action" in json_payload:
            lines.append(f"Action: {json_payload['action']}")
        if "http_status" in json_payload:
            lines.append(f"HTTP status: {json_payload['http_status']}")
        if isinstance(json_payload.get("result"), list):
            lines.append(f"Result count: {len(json_payload['result'])}")
        if isinstance(json_payload.get("tasks"), list):
            lines.append(f"Tasks count: {len(json_payload['tasks'])}")
        extracted_packages = extract_package_items(json_payload)
        if extracted_packages:
            lines.append(f"Extracted package count: {len(extracted_packages)}")
        update_package_count = extract_update_package_count(json_payload)
        if update_package_count is not None:
            lines.append(f"Update package count: {update_package_count}")
        if "output" in json_payload:
            lines.append(f"Output excerpt: {build_excerpt(json_payload['output'])}")

    if command_result["parse_error"]:
        lines.append(f"JSON parse error: {command_result['parse_error']}")

    if command_result["stderr"]:
        lines.append(f"Stderr: {build_excerpt(command_result['stderr'])}")

    if command_result["stdout"] and (command_result["parse_error"] or not isinstance(json_payload, dict)):
        lines.append(f"Stdout excerpt: {build_excerpt(command_result['stdout'])}")

    return "\n".join(lines)


def build_excerpt(value: object, max_length: int = 600) -> str:
    if isinstance(value, dict):
        text = json.dumps(value, ensure_ascii=True)
    elif isinstance(value, list):
        text = json.dumps(value, ensure_ascii=True)
    else:
        text = str(value)

    normalized = " ".join(text.split())
    if len(normalized) <= max_length:
        return normalized
    return normalized[:max_length] + "..."


def build_package_source_summary(
    label: str,
    total_count: int,
    matched_packages: list[dict],
    prefix: str,
) -> str:
    return "\n".join(
        [
            f"Strategy [{label}] summary:",
            *build_filter_summary_lines(total_count, matched_packages, prefix),
        ]
    )


def format_direct_repo_result(repo_result: dict, prefix: str) -> str:
    lines = ["Strategy [WaptRemoteRepo]: direct repository access via native WAPT Python API"]

    if repo_result.get("endpoint_url"):
        lines.append(f"Endpoint URL: {repo_result['endpoint_url']}")

    if repo_result.get("source"):
        lines.append(f"Client auth source: {repo_result['source']}")

    lines.append(f"Status code: {repo_result.get('status_code')}")

    lines.append(f"Succeeded: {repo_result['ok']}")

    if repo_result["ok"]:
        packages = repo_result["packages"]
        matched_packages = filter_packages(packages, prefix)
        lines.extend(build_filter_summary_lines(len(packages), matched_packages, prefix))
        lines.append(f"Discarded packages: {repo_result['discarded_count']}")
    elif repo_result.get("error"):
        lines.append(f"Error: {build_excerpt(repo_result['error'])}")

    return "\n".join(lines)


def build_final_strategy_summary(
    strategy: str,
    total_count: int,
    matched_packages: list[dict],
    prefix: str,
) -> str:
    return "\n".join(
        [
            "Final strategy summary:",
            f"Selected strategy: {strategy}",
            *build_filter_summary_lines(total_count, matched_packages, prefix),
        ]
    )


def build_filter_summary_lines(total_count: int, matched_packages: list[dict], prefix: str) -> list[str]:
    return [
        f"Total packages before filter: {total_count}",
        f"Filter field used: {FILTER_FIELD_USED}",
        f"Filter mode used: {FILTER_MODE_USED}",
        f"Filter value used: {prefix}",
        f"Total packages matching filter: {len(matched_packages)}",
        f"First matching package_ids: {build_matching_package_ids_excerpt(matched_packages)}",
    ]


def build_matching_package_ids_excerpt(matched_packages: list[dict], max_items: int = 10) -> str:
    if not matched_packages:
        return "<none>"

    package_ids = [package.get("package_id", "") for package in matched_packages]
    package_ids = [package_id for package_id in package_ids if str(package_id).strip()]
    if not package_ids:
        return "<none>"

    excerpt = package_ids[:max_items]
    suffix = "" if len(package_ids) <= max_items else ", ..."
    return ", ".join(excerpt) + suffix


def build_match_message(matched_count: int, prefix: str, source: str) -> str:
    return f"{matched_count} paquet(s) dont package_id contient '{prefix}' ont ete recuperes {source}."


def build_no_match_message(prefix: str) -> str:
    return f"Aucun paquet dont package_id contient '{prefix}' n'a ete trouve."


def build_response(
    success: bool,
    message: str,
    packages: list[dict],
    technical_details: list[str],
) -> dict:
    return {
        "success": success,
        "message": message,
        "packages": packages,
        "technical_details": "\n\n".join(part.strip() for part in technical_details if str(part).strip()),
    }


def get_preferred_text_encoding() -> str:
    return locale.getpreferredencoding(False) or "utf-8"


if __name__ == "__main__":
    raise SystemExit(main())