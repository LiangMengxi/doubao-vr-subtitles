from unittest import TestCase, main
from unittest.mock import patch
from pathlib import Path
import cv2
import numpy as np
from window_source import Window
from subtitle_launcher import request_caption_close, SubtitleCloser

CAPTION = Window(123, 456, "", "Chrome_WidgetWin_1", 640, 151, 0x200088)


class CaptionCloseTests(TestCase):
    @patch("subtitle_launcher.still_same_window", return_value=True)
    @patch("subtitle_launcher.user32")
    def test_verified_icon_posts_only_to_caption(self, api, same):
        reference = cv2.imread(str(Path(__file__).parent / "assets" / "caption-close.png"), cv2.IMREAD_UNCHANGED)
        frame = np.full((151, 640, 4), 52, dtype=np.uint8)
        frame[:, :, 3] = 255
        frame[10:10+reference.shape[0], 590:590+reference.shape[1]] = reference
        api.GetDpiForWindow.return_value = 96
        def client_rect(hwnd, pointer):
            pointer._obj.right = 640
            pointer._obj.bottom = 151
            return True
        api.GetClientRect.side_effect = client_rect
        api.PostMessageW.return_value = True
        request_caption_close(CAPTION, frame)
        calls = api.PostMessageW.call_args_list
        self.assertEqual([call.args[1] for call in calls], [0x200, 0x201, 0x202])
        self.assertTrue(all(call.args[0] == CAPTION.hwnd for call in calls))

    @patch("subtitle_launcher.still_same_window", return_value=True)
    @patch("subtitle_launcher.user32")
    def test_missing_close_icon_never_sends_mouse_messages(self, api, same):
        api.GetDpiForWindow.return_value = 96
        frame = np.full((151, 640, 4), 52, dtype=np.uint8)
        frame[:, :, 3] = 255
        with self.assertRaisesRegex(RuntimeError, "未识别到"):
            request_caption_close(CAPTION, frame)
        api.PostMessageW.assert_not_called()

    @patch("subtitle_launcher.user32")
    def test_main_window_is_never_closed(self, api):
        main_window = Window(123, 456, "豆包", "Chrome_WidgetWin_1", 1200, 800, 0x200100)
        with self.assertRaises(RuntimeError):
            request_caption_close(main_window)
        api.PostMessageW.assert_not_called()

    @patch("subtitle_launcher.still_same_window", return_value=False)
    @patch("subtitle_launcher.user32")
    def test_reused_handle_is_not_sent_a_close_message(self, api, same):
        request_caption_close(CAPTION)
        api.PostMessageW.assert_not_called()

    @patch("subtitle_launcher.request_caption_close")
    @patch("subtitle_launcher.still_same_window", return_value=True)
    @patch("subtitle_launcher.user32")
    def test_hidden_caption_completes_close(self, api, same, request):
        api.IsWindowVisible.return_value = False
        closer = SubtitleCloser(CAPTION)
        self.assertTrue(closer.poll())

    @patch("subtitle_launcher.time.monotonic", return_value=100)
    @patch("subtitle_launcher.request_caption_close")
    @patch("subtitle_launcher.still_same_window", return_value=True)
    @patch("subtitle_launcher.user32")
    def test_unacknowledged_close_times_out(self, api, same, request, clock):
        api.IsWindowVisible.return_value = True
        closer = SubtitleCloser(CAPTION)
        self.assertFalse(closer.poll())
        clock.return_value = 106
        with self.assertRaisesRegex(RuntimeError, "未关闭字幕"):
            closer.poll()


if __name__ == "__main__":
    main()
