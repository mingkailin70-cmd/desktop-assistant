import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


WORKER = Path(__file__).resolve().parents[1] / "src" / "XiaoK.Voice" / "voice_worker.py"


class VoiceWorkerMemoryErrorTests(unittest.TestCase):
    def run_worker(self, module_sources: dict[str, str], input_text: str = "") -> subprocess.CompletedProcess[str]:
        with tempfile.TemporaryDirectory(prefix="xiaok-voice-memory-") as directory:
            root = Path(directory)
            for name, source in module_sources.items():
                (root / name).write_text(source, encoding="utf-8")
            environment = os.environ.copy()
            environment["PYTHONPATH"] = str(root)
            return subprocess.run(
                [sys.executable, str(WORKER), "--task", "tts", "--model-dir", str(root)],
                input=input_text,
                capture_output=True,
                text=True,
                encoding="utf-8",
                timeout=10,
                env=environment,
                check=False,
            )

    def test_model_load_memory_error_has_typed_startup_code(self) -> None:
        result = self.run_worker({"torch.py": "raise MemoryError()\n"})
        self.assertEqual(2, result.returncode)
        self.assertEqual({"type": "ready", "ok": False, "code": "LOW_SYSTEM_MEMORY"}, json.loads(result.stdout))

    def test_inference_memory_error_has_typed_request_code(self) -> None:
        result = self.run_worker(
            {
                "torch.py": "float32 = object()\n",
                "numpy.py": "",
                "soundfile.py": "",
                "qwen_tts.py": """
class Model:
    def generate_custom_voice(self, **kwargs):
        raise MemoryError()

class Qwen3TTSModel:
    @staticmethod
    def from_pretrained(*args, **kwargs):
        return Model()
""",
            },
            '{"command":"synthesize","text":"合成测试"}\n',
        )
        lines = [json.loads(line) for line in result.stdout.splitlines()]
        self.assertEqual(3, result.returncode)
        self.assertEqual({"type": "ready", "ok": True}, lines[0])
        self.assertEqual({"ok": False, "code": "LOW_SYSTEM_MEMORY"}, lines[1])


if __name__ == "__main__":
    unittest.main()
