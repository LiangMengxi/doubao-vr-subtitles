import ctypes
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from subtitle_launcher import INPUT, SubtitleLauncher, parse_shortcut


class CaptionLauncherTests(unittest.TestCase):
    def test_shortcut_parser(self):
        self.assertEqual(parse_shortcut("Alt+Z"), [0x12, ord("Z")])
        self.assertEqual(parse_shortcut("Ctrl+Shift+F5"), [0x11, 0x10, 0x74])
        for invalid in ["", "Z", "Alt", "Win+Z", "Ctrl+Ctrl+Z", "Alt+bad-command"]:
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                parse_shortcut(invalid)

    def test_input_layout_matches_x64_win32(self):
        self.assertEqual(ctypes.sizeof(INPUT), 40)

    @patch("subtitle_launcher.send_shortcut")
    @patch("subtitle_launcher.doubao_windows")
    def test_existing_caption_never_toggles_shortcut(self, windows, send):
        caption = SimpleNamespace(score=180, hwnd=123)
        windows.return_value = [caption]
        launcher = SubtitleLauncher()
        self.assertIs(launcher.poll(), caption)
        send.assert_not_called()

    @patch("subtitle_launcher.read_caption_shortcut", return_value="Alt+Z")
    @patch("subtitle_launcher.send_shortcut", return_value=True)
    @patch("subtitle_launcher.running_doubao_exe", return_value=Path("Doubao.exe"))
    @patch("subtitle_launcher.doubao_windows", return_value=[])
    def test_pending_launch_sends_only_once(self, windows, running, send, shortcut):
        launcher = SubtitleLauncher()
        self.assertIsNone(launcher.poll())
        self.assertIsNone(launcher.poll())
        send.assert_called_once_with("Alt+Z")

    @patch("subtitle_launcher.read_caption_shortcut", return_value="Alt+Z")
    @patch("subtitle_launcher.send_shortcut", side_effect=[False, True])
    @patch("subtitle_launcher.running_doubao_exe", return_value=Path("Doubao.exe"))
    @patch("subtitle_launcher.doubao_windows", return_value=[])
    def test_held_modifier_defers_and_then_sends(self, windows, running, send, shortcut):
        launcher = SubtitleLauncher()
        launcher.poll()
        self.assertFalse(launcher.sent)
        launcher.poll()
        self.assertTrue(launcher.sent)

    @patch("subtitle_launcher.send_shortcut")
    @patch("subtitle_launcher.running_doubao_exe", return_value=Path("Doubao.exe"))
    @patch("subtitle_launcher.doubao_windows", return_value=[])
    def test_timeout_reports_failure_without_sending(self, windows, running, send):
        launcher = SubtitleLauncher()
        launcher.deadline = 0
        with self.assertRaisesRegex(RuntimeError, "未能唤起字幕"):
            launcher.poll()
        send.assert_not_called()

    @patch("subtitle_launcher.time.monotonic", return_value=100)
    @patch("subtitle_launcher.subprocess.Popen")
    @patch("subtitle_launcher.installed_doubao_exe", return_value=Path("D:/Apps/Doubao.exe"))
    @patch("subtitle_launcher.read_caption_shortcut", return_value="Alt+Z")
    @patch("subtitle_launcher.send_shortcut", return_value=True)
    @patch("subtitle_launcher.running_doubao_exe", return_value=None)
    @patch("subtitle_launcher.doubao_windows", return_value=[])
    def test_cold_start_waits_before_shortcut(self, windows, running, send, shortcut, installed, spawn, clock):
        launcher = SubtitleLauncher()
        spawn.assert_called_once()
        command = spawn.call_args.args[0]
        self.assertIn("--launch_from=auto_run", command)
        self.assertIn("--no-startup-window", command)
        self.assertEqual(spawn.call_args.kwargs["startupinfo"].wShowWindow, 0)
        self.assertIsNone(launcher.poll())
        send.assert_not_called()
        clock.return_value = 105
        running.return_value = Path("D:/Apps/Doubao.exe")
        # No visible main window is expected during background startup.
        windows.return_value = []
        launcher.poll()
        send.assert_called_once_with("Alt+Z")


if __name__ == "__main__":
    unittest.main()
