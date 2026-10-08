import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import download_locked_models


def model(status: str, *, required: bool = True, checksum: str = "a" * 64) -> dict:
    return {
        "requiredForP0": required,
        "status": status,
        "files": [{"upstreamReportedSizeBytes": 1, "localVerifiedSha256": checksum}],
    }


class P0ModelAssetStatusTests(unittest.TestCase):
    def test_locally_evaluated_assets_still_count_as_downloaded_and_verified(self) -> None:
        lock = {
            "models": [
                model("downloaded_and_verified"),
                model("locally_evaluated"),
                model("blocked-license-review", required=False, checksum=""),
            ]
        }
        self.assertEqual(
            download_locked_models.ROOT_ASSETS_READY,
            download_locked_models.derive_p0_model_asset_status(lock),
        )

    def test_incomplete_required_model_keeps_root_status_partial(self) -> None:
        lock = {"models": [model("downloaded_and_verified"), model("partially_downloaded")]}
        self.assertEqual(
            download_locked_models.ROOT_ASSETS_PARTIAL,
            download_locked_models.derive_p0_model_asset_status(lock),
        )

    def test_missing_or_invalid_required_file_hash_keeps_root_status_partial(self) -> None:
        lock = {"models": [model("downloaded_and_verified", checksum="invalid")]}
        self.assertEqual(
            download_locked_models.ROOT_ASSETS_PARTIAL,
            download_locked_models.derive_p0_model_asset_status(lock),
        )

    def test_empty_required_set_does_not_claim_assets_ready(self) -> None:
        lock = {"models": [model("blocked-license-review", required=False)]}
        self.assertEqual(
            download_locked_models.ROOT_ASSETS_PARTIAL,
            download_locked_models.derive_p0_model_asset_status(lock),
        )


if __name__ == "__main__":
    unittest.main()
