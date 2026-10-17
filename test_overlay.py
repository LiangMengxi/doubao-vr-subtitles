"""Regressions for frame continuity and independent teardown failures."""
import ctypes
import threading
import unittest
from unittest.mock import Mock, patch
from vr_overlay import SubtitleOverlay
from window_source import Window
from app import BridgeApp


def overlay_stub():
    overlay = object.__new__(SubtitleOverlay)
    overlay.handle, overlay.initialized, overlay.shown = 1, True, False
    overlay.api, overlay.pool = Mock(), Mock()
    overlay.pool.upload.return_value = ctypes.c_void_p(1234)
    return overlay


class OverlayTests(unittest.TestCase):
    def test_each_submission_flushes_after_openvr_copy(self):
        overlay = overlay_stub()
        order = []
        overlay.pool.upload.side_effect = lambda _: (order.append('upload') or ctypes.c_void_p(1234))
        overlay.api.setOverlayTexture.side_effect = lambda *_: order.append('submit')
        overlay.pool.flush.side_effect = lambda *_: order.append('flush')
        overlay.api.showOverlay.side_effect = lambda *_: order.append('show')
        overlay.submit(object())
        overlay.submit(object())
        self.assertEqual(order, ['upload', 'submit', 'flush', 'show', 'upload', 'submit', 'flush'])

    def test_updates_do_not_hide_or_repeatedly_show(self):
        overlay = overlay_stub()
        for _ in range(30):
            self.assertTrue(overlay.submit(object()))
        self.assertEqual(overlay.api.setOverlayTexture.call_count, 30)
        overlay.api.showOverlay.assert_called_once_with(1)
        overlay.api.hideOverlay.assert_not_called()
        overlay.api.clearOverlayTexture.assert_not_called()
        overlay.api.setOverlayRaw.assert_not_called()

    def test_upload_timeout_preserves_previous_image(self):
        overlay = overlay_stub()
        overlay.pool.upload.return_value = None
        self.assertFalse(overlay.submit(object()))
        overlay.api.setOverlayTexture.assert_not_called()
        overlay.api.hideOverlay.assert_not_called()
        overlay.api.clearOverlayTexture.assert_not_called()
        overlay.pool.flush.assert_not_called()

    @patch('vr_overlay.openvr.shutdown')
    def test_cleanup_continues_after_independent_failures(self, shutdown):
        overlay = overlay_stub()
        api, pool = overlay.api, overlay.pool
        api.hideOverlay.side_effect = RuntimeError('runtime disconnected')
        pool.close.side_effect = RuntimeError('device removed')
        with self.assertLogs(level='ERROR'):
            overlay.close()
        api.clearOverlayTexture.assert_called_once_with(1)
        api.destroyOverlay.assert_called_once_with(1)
        shutdown.assert_called_once()
        overlay.close()
        shutdown.assert_called_once()

    def test_capture_stop_cannot_delay_overlay_removal(self):
        app = object.__new__(BridgeApp)
        app.overlay, app.capture = Mock(), Mock()
        overlay, capture = app.overlay, app.capture
        app.capture_stoppers = []
        release = threading.Event()
        def blocked_stop():
            overlay.close.assert_called_once()
            release.wait(2)
        capture.stop.side_effect = blocked_stop
        try:
            app.cleanup()
            overlay.close.assert_called_once()
            self.assertIsNone(app.overlay)
            self.assertIsNone(app.capture)
            app.cleanup()
            overlay.close.assert_called_once()
        finally:
            release.set()
            for worker in app.capture_stoppers:
                worker.join(2)

    def test_expanded_caption_stays_a_candidate(self):
        for height in (151, 400, 520, 800):
            self.assertGreaterEqual(Window(1, 1, '', 'Chrome_WidgetWin_1', 640, height, 0x80).score, 150)
        self.assertLess(Window(1, 1, '豆包', 'Chrome_WidgetWin_1', 1201, 800, 0).score, 150)
        self.assertLess(Window(1, 1, '', 'Chrome_WidgetWin_1', 60, 60, 0x80).score, 150)


if __name__ == '__main__':
    unittest.main()
