/**
 * LucidSupport Admin — postMessage bridge for live preview iframe.
 *
 * Admin page sends commands to the widget inside the iframe.
 * Widget sends state updates back to the admin page.
 */

(function () {
  // Listen for messages from the admin page (when this script is loaded IN the iframe)
  if (window.parent !== window) {
    window.addEventListener('message', (e) => {
      if (e.data?.source !== 'lucidsupport-admin') return;

      const widget = document.querySelector('lucidsupport-widget')?.__widget;
      if (!widget) return;

      if (e.data.command) {
        handleCommand(widget, e.data.command);
      }
      if (e.data.signal) {
        handleSignal(widget, e.data.signal);
      }
    });
  }

  function handleCommand(widget, cmd) {
    switch (cmd) {
      case 'trigger_toast':
        widget.dispatch?.('condition_trigger');
        break;
      case 'open_panel':
        widget.dispatch?.('fab_click');
        break;
      case 'simulate_struggling':
        widget.dispatch?.('frustration_threshold');
        break;
      case 'activate_guide':
        widget.dispatch?.('accept_guide');
        break;
    }
  }

  function handleSignal(widget, signal) {
    if (widget.frustration?.recordSignal) {
      widget.frustration.recordSignal(signal);
    }
  }

  // Relay widget state changes back to admin
  function setupRelay() {
    const original = window.dispatchEvent.bind(window);
    document.addEventListener('lucidsupport:statechange', (e) => {
      window.parent.postMessage({
        source: 'lucidsupport-widget',
        event: 'state_change',
        ...e.detail
      }, '*');
    });
  }

  if (window.parent !== window) {
    setupRelay();
  }
})();
