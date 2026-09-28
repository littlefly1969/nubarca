'use strict';
// Every network name is somebody else's text: anyone nearby can broadcast one.
// It only ever reaches the page through textContent, never as HTML.
(function () {
  var $ = function (id) { return document.getElementById(id); };
  var attempted = null;

  var NETWORK = {
    'connected': 'Connected',
    'access-point': 'Setup mode',
    'starting': 'Looking for a network…',
    'disconnected': 'Not connected'
  };
  var PRINTER = {
    ready: 'Ready', printing: 'Printing', offline: 'Offline', error: 'Error', unknown: 'Unknown'
  };

  function show(id, text, tone) {
    var el = $(id);
    el.textContent = text;
    el.className = tone || '';
  }

  function render(s) {
    show('network', NETWORK[s.network] || s.network, s.network === 'connected' ? 'ok' : '');
    var connection = s.network === 'access-point'
      ? s.setupNetwork
      : s.connection ? s.connection + (s.connectionType === 'ethernet' ? ' (Ethernet)' : '') : 'None';
    show('connection', connection);
    show('address', s.address || '—');
    if (s.printer) {
      show('printer', s.printer.name + ' · ' + (PRINTER[s.printer.state] || s.printer.state),
        s.printer.state === 'ready' || s.printer.state === 'printing' ? 'ok' : 'bad');
    } else {
      show('printer', 'Not found', 'bad');
    }
    show('cups', s.cups === 'ready' ? 'Ready' : s.cups === 'not-used' ? 'Not used' : 'Unavailable',
      s.cups === 'ready' ? 'ok' : s.cups === 'not-used' ? '' : 'bad');
    show('nubarca', s.nubarca === 'connected' ? 'Connected'
      : s.network === 'connected' ? 'Not reachable yet' : 'Waiting for network',
      s.nubarca === 'connected' ? 'ok' : '');

    var a = s.lastAttempt;
    if (a && a.outcome === 'failed' && (attempted === null || attempted === a.ssid)) {
      message('Connection to ' + a.ssid + ' failed. Check the password and try again.', 'bad');
      busy(false);
    }
  }

  function message(text, tone) { show('message', text, tone); }
  function busy(on) { $('submit').disabled = on; }

  function refreshStatus() {
    fetch('/setup/status', { cache: 'no-store' })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (s) { if (s) render(s); })
      .catch(function () { /* the setup network may be closing */ });
  }

  function refreshNetworks() {
    fetch('/setup/wifi', { cache: 'no-store' })
      .then(function (r) { return r.ok ? r.json() : []; })
      .then(function (list) {
        var ul = $('networks');
        while (ul.firstChild) ul.removeChild(ul.firstChild);
        list.forEach(function (n) {
          var li = document.createElement('li');
          var button = document.createElement('button');
          button.type = 'button';
          var name = document.createElement('span');
          name.textContent = (n.secure ? '🔒 ' : '') + n.ssid;
          var signal = document.createElement('span');
          signal.className = 'muted';
          signal.textContent = n.signal + '%';
          button.appendChild(name);
          button.appendChild(signal);
          button.addEventListener('click', function () {
            $('ssid').value = n.ssid;
            $('password').focus();
          });
          li.appendChild(button);
          ul.appendChild(li);
        });
        $('no-networks').hidden = list.length > 0;
      })
      .catch(function () { $('no-networks').hidden = false; });
  }

  $('rescan').addEventListener('click', refreshNetworks);

  $('connect').addEventListener('submit', function (event) {
    event.preventDefault();
    var ssid = $('ssid').value;
    var password = $('password').value;
    attempted = ssid;
    busy(true);
    message('Connecting to ' + ssid + '…');
    fetch('/setup/wifi/connect', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ssid: ssid, password: password })
    })
      .then(function (r) { return r.json().then(function (body) { return { status: r.status, body: body }; }); })
      .then(function (r) {
        $('password').value = '';
        if (r.status === 202) {
          message('Connecting to ' + ssid + '… The NubArca setup network will now close. '
            + 'If it appears again, the connection failed: join it again and reopen this page.', 'ok');
          return;
        }
        busy(false);
        var reasons = {
          invalid_ssid: 'That Wi-Fi name is not valid.',
          invalid_password: 'A Wi-Fi password has 8 to 63 characters.',
          busy: 'Already connecting. Wait a moment.',
          not_in_setup_mode: 'The box is already connected to a network.'
        };
        message(reasons[r.body && r.body.error] || 'The request was not accepted.', 'bad');
      })
      .catch(function () {
        // The setup network closed before the answer arrived: that is the
        // normal hand-off, not an error.
        $('password').value = '';
        message('Connecting to ' + ssid + '… The NubArca setup network will now close.', 'ok');
      });
  });

  refreshStatus();
  refreshNetworks();
  setInterval(refreshStatus, 3000);
})();
