from __future__ import annotations

import argparse
import base64
import getpass
import json
import locale
import os
import secrets
import sys
import traceback
from pathlib import Path


LOCAL_SERVICE_BASE_URL = "https://127.0.0.1:8088"
LOCAL_SERVICE_CATALOG_ENDPOINTS = [
    "/packages.json?latest=1&all_sections=1&limit=5000",
    "/packages?latest=1&all_sections=1&limit=5000",
    "/list?latest=1&all_sections=1&limit=5000",
]
FILTER_FIELD_USED = "package_id"
FILTER_MODE_USED = "contains"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="WAPT local service machine-context helper")
    parser.add_argument("--request-path", required=True)
    parser.add_argument("--output-path", required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    response = execute_helper(Path(args.request_path), Path(args.output_path))
    write_output_atomically(Path(args.output_path), response)
    return 0 if response.get("success") else 1


def execute_helper(request_path: Path, output_path: Path) -> dict:
    technical_details: list[str] = []
    request_payload: dict = {}

    try:
        request_payload = json.loads(request_path.read_text(encoding="utf-8-sig"))
    except Exception:
        return {
            "request_id": "",
            "success": False,
            "message": "Le helper machine n'a pas pu lire la requete.",
            "packages": [],
            "total_count": 0,
            "matched_count": 0,
            "selected_endpoint": None,
            "context_used": collect_identity_context(),
            "technical_details": traceback.format_exc().strip(),
        }

    request_id = str(request_payload.get("request_id") or "").strip()
    prefix = str(request_payload.get("prefix") or "").strip()
    timeout_seconds = max(1, int(request_payload.get("timeout_seconds") or 30))
    context_used = collect_identity_context()

    technical_details.append("Helper strategy: local service machine-context catalog retrieval")
    technical_details.append(f"Python executable: {sys.executable}")
    technical_details.append(f"Request path: {request_path}")
    technical_details.append(f"Output path: {output_path}")
    technical_details.append(f"Filter field used: {FILTER_FIELD_USED}")
    technical_details.append(f"Filter mode used: {FILTER_MODE_USED}")
    technical_details.append(f"Filter value used: {prefix}")
    technical_details.append(f"Timeout: {timeout_seconds} second(s)")
    technical_details.append(f"Execution identity: {context_used['username']}")
    technical_details.append(f"User domain: {context_used['userdomain']}")

    verify_option = resolve_local_service_verify()
    technical_details.append(
        f"TLS verify: {verify_option if verify_option else '<disabled>'}"
    )

    machine_name = (os.environ.get("COMPUTERNAME") or "").strip()
    if not machine_name:
        return build_response(
            request_id=request_id,
            success=False,
            message="Le helper machine ne peut pas determiner le nom de la machine.",
            packages=[],
            selected_endpoint=None,
            context_used=context_used,
            technical_details=technical_details,
            prefix=prefix,
        )

    machine_username = f"{machine_name}$"
    client_secret = secrets.token_hex(32)
    technical_details.append(f"Login identity: {machine_username}")

    login_result = perform_local_service_request(
        endpoint="/login",
        timeout_seconds=timeout_seconds,
        verify_option=verify_option,
        method="POST",
        json_body={"secret": client_secret},
        basic_username=machine_username,
    )
    technical_details.append(f"Login URL: {login_result['url']}")
    technical_details.append(f"Login status code: {login_result['status_code']}")
    technical_details.append(
        f"Login response excerpt: {build_excerpt(login_result['response_excerpt'] or '<empty>')}"
    )

    login_payload = login_result["json_payload"] if isinstance(login_result["json_payload"], dict) else {}
    token_result = obtain_local_service_bearer_token(login_payload, client_secret)
    technical_details.append(f"Token mode: {token_result['mode'] or '<none>'}")
    technical_details.append(f"Token source: {token_result['source'] or '<none>'}")
    technical_details.append(f"Token accessible: {token_result['accessible']}")
    technical_details.append(f"Token acquisition: {token_result['acquisition']}")
    if token_result["error"]:
        technical_details.append(f"Token error: {build_excerpt(token_result['error'])}")

    bearer_token = token_result["token"]
    if not bearer_token:
        return build_response(
            request_id=request_id,
            success=False,
            message="Le helper machine n'a pas pu obtenir de Bearer utilisable pour le service local.",
            packages=[],
            selected_endpoint=None,
            context_used=context_used,
            technical_details=technical_details,
            prefix=prefix,
        )

    selected_endpoint = None
    packages: list[dict] = []
    matched_packages: list[dict] = []
    for endpoint in LOCAL_SERVICE_CATALOG_ENDPOINTS:
        request_result = perform_local_service_request(
            endpoint=endpoint,
            timeout_seconds=timeout_seconds,
            verify_option=verify_option,
            bearer_token=bearer_token,
        )
        endpoint_packages = normalize_packages(extract_package_items(request_result["json_payload"]))
        endpoint_matches = filter_packages(endpoint_packages, prefix)
        technical_details.append(f"Catalog URL: {request_result['url']}")
        technical_details.append(f"Catalog status code: {request_result['status_code']}")
        technical_details.append(
            f"Catalog response excerpt: {build_excerpt(request_result['response_excerpt'] or '<empty>')}"
        )
        technical_details.append(
            f"Catalog total packages before filter: {len(endpoint_packages)}"
        )
        technical_details.append(
            f"Catalog packages matching filter: {len(endpoint_matches)}"
        )
        if request_result["parse_error"]:
            technical_details.append(f"Catalog JSON parse error: {request_result['parse_error']}")
        if request_result["error"]:
            technical_details.append(f"Catalog error: {build_excerpt(request_result['error'])}")

        if endpoint_packages:
            selected_endpoint = request_result["url"]
            packages = endpoint_packages
            matched_packages = endpoint_matches
            break

    if not packages:
        return build_response(
            request_id=request_id,
            success=False,
            message="Le helper machine n'a pas pu lire de catalogue complet depuis le service local.",
            packages=[],
            selected_endpoint=selected_endpoint,
            context_used=context_used,
            technical_details=technical_details,
            prefix=prefix,
        )

    return {
        "request_id": request_id,
        "prefix": prefix,
        "success": True,
        "message": build_match_message(
            len(matched_packages),
            prefix,
            "via le helper machine du service WAPT local",
        ) if matched_packages else build_no_match_message(prefix),
        "packages": packages,
        "total_count": len(packages),
        "matched_count": len(matched_packages),
        "selected_endpoint": selected_endpoint,
        "context_used": context_used,
        "technical_details": "\n".join(technical_details),
    }


def build_response(
    request_id: str,
    success: bool,
    message: str,
    packages: list[dict],
    selected_endpoint: str | None,
    context_used: dict,
    technical_details: list[str],
    prefix: str,
) -> dict:
    matched_packages = filter_packages(packages, prefix)
    final_technical_details = [part.strip() for part in technical_details if str(part).strip()]
    final_technical_details.extend(build_filter_summary_lines(len(packages), matched_packages, prefix))
    return {
        "request_id": request_id,
        "prefix": prefix,
        "success": success,
        "message": message,
        "packages": packages,
        "total_count": len(packages),
        "matched_count": len(matched_packages),
        "selected_endpoint": selected_endpoint,
        "context_used": context_used,
        "technical_details": "\n".join(final_technical_details),
    }


def collect_identity_context() -> dict:
    return {
        "username": os.environ.get("USERNAME") or getpass.getuser() or "<unknown>",
        "userdomain": os.environ.get("USERDOMAIN") or "<unknown>",
    }


def build_local_service_url(endpoint: str) -> str:
    return f"{LOCAL_SERVICE_BASE_URL}/{endpoint.lstrip('/')}"


def resolve_local_service_verify() -> object:
    candidates = [
        Path(sys.executable).resolve().parent / "public" / "localservice.crt",
        Path(r"C:\Program Files (x86)\wapt\public\localservice.crt"),
    ]
    for candidate in candidates:
        if candidate.is_file():
            return str(candidate)
    return False


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
            "json_payload": None,
            "response_excerpt": "",
            "parse_error": None,
            "error": traceback.format_exc().strip(),
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
        "json_payload": json_payload,
        "response_excerpt": build_excerpt(json_payload if json_payload is not None else response_text),
        "parse_error": parse_error,
        "error": None,
    }


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
                    "Encrypted token filepath returned by /login, but the helper process cannot read that file."
                )
                return token_result

            token_result["accessible"] = True
            token_result["token"] = decrypt_local_service_token(encrypted_token, client_secret)
            token_result["acquisition"] = (
                "Encrypted token filepath returned by /login, read by the helper, decrypted with the same client secret, then sent as Authorization: Bearer <token>."
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


def write_output_atomically(output_path: Path, payload: dict) -> None:
    output_path.parent.mkdir(parents=True, exist_ok=True)
    temporary_path = output_path.with_suffix(output_path.suffix + ".tmp")
    temporary_path.write_text(
        json.dumps(payload, ensure_ascii=True),
        encoding="utf-8",
    )
    temporary_path.replace(output_path)


def get_preferred_text_encoding() -> str:
    return locale.getpreferredencoding(False) or "utf-8"


if __name__ == "__main__":
    raise SystemExit(main())