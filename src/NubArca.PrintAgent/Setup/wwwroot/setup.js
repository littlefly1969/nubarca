'use strict';
// Every network name is somebody else's text: anyone nearby can broadcast one.
// It only ever reaches the page through textContent, never as HTML.
(function () {
  var $ = function (id) { return document.getElementById(id); };
  var attempted = null;
  var sending = false;
  // What the box last said: whether the Wi-Fi can be configured now (setup
  // mode, or Ethernet with the Wi-Fi free) and if not why, and whether the
  // page is served over the setup network.
  var wifiAvailable = true;
  var unavailableReason = null;
  var onSetupNetwork = false;
  // Status requests are numbered: one sent before the box accepted an attempt
  // may still carry the previous attempt's outcome, and must not end this one.
  var polls = 0;
  var answersFrom = 0;
  var UNAVAILABLE = {
    'wifi-in-use': 'The box is using its Wi-Fi: it cannot be changed from this page.',
    'no-wifi': 'This box has no Wi-Fi adapter.',
    'not-ready': 'The box is still looking for a network. Try again in a minute.',
    'network-manager': 'The box\'s network service is not running.'
  };

  function unavailableText() {
    return UNAVAILABLE[unavailableReason] || 'Wi-Fi configuration is unavailable right now.';
  }

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

  function render(s, seq) {
    show('network', NETWORK[s.network] || s.network, s.network === 'connected' ? 'ok' : '');
    var connection = s.network === 'access-point'
      ? s.setupNetwork
      : s.connection ? s.connection + (s.connectionType === 'ethernet' ? ' (Ethernet)' : '') : 'None';
    show('connection', connection);
    show('address', s.address || '—');
    if (s.printer) {
      // A simulated printer says so on its own line: its sheets are files.
      show('printer', s.printer.name + ' · ' + (PRINTER[s.printer.state] || s.printer.state)
        + (s.printer.simulated ? ' · Simulated: prints go to a file, not to paper' : ''),
        s.printer.state === 'ready' || s.printer.state === 'printing' ? 'ok' : 'bad');
    } else {
      show('printer', 'Not found', 'bad');
    }
    // The printer's own count of the prints left on its media. Absent when
    // the printer reports none, or is offline — never an estimate.
    var remaining = s.printer && typeof s.printer.remainingPrints === 'number' ? s.printer.remainingPrints : null;
    show('remaining', remaining === null ? 'Not available' : String(remaining),
      remaining === 0 ? 'bad' : '');
    show('cups', s.cups === 'ready' ? 'Ready' : s.cups === 'not-used' ? 'Not used' : 'Unavailable',
      s.cups === 'ready' ? 'ok' : s.cups === 'not-used' ? '' : 'bad');
    show('nubarca', s.nubarca === 'connected' ? 'Connected'
      : s.network === 'connected' ? 'Not reachable yet' : 'Waiting for network',
      s.nubarca === 'connected' ? 'ok' : '');

    onSetupNetwork = s.network === 'access-point';
    var available = s.wifiConfigurationAvailable === true;
    var becameAvailable = available && !wifiAvailable;
    wifiAvailable = available;
    unavailableReason = s.wifiConfigurationUnavailable || null;
    applyAvailability();
    if (becameAvailable) refreshNetworks();

    var a = s.lastAttempt;
    var current = !sending || seq >= answersFrom;
    if (a && a.outcome === 'failed' && (attempted === null || attempted === a.ssid) && current) {
      message('Connection to ' + a.ssid + ' failed. Check the password and try again.', 'bad');
      busy(false);
    } else if (a && a.outcome === 'connected' && attempted === a.ssid && sending && current) {
      message('Connected to ' + a.ssid + '.', 'ok');
      busy(false);
    }
  }

  function message(text, tone) { show('message', text, tone); }

  function busy(on) {
    sending = on;
    applyAvailability();
  }

  // The form, the list and the refresh button work only while the box can
  // configure its Wi-Fi; otherwise they are disabled and say why.
  function applyAvailability() {
    $('wifi-unavailable').hidden = wifiAvailable;
    $('wifi-unavailable').textContent = unavailableText();
    $('rescan').disabled = !wifiAvailable;
    $('ssid').disabled = !wifiAvailable;
    $('password').disabled = !wifiAvailable;
    $('submit').disabled = !wifiAvailable || sending;
    var buttons = $('networks').getElementsByTagName('button');
    for (var i = 0; i < buttons.length; i++) buttons[i].disabled = !wifiAvailable;
  }

  function refreshStatus() {
    var seq = ++polls;
    fetch('/setup/status', { cache: 'no-store' })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (s) { if (s) render(s, seq); })
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
        $('no-networks').hidden = list.length > 0 || !wifiAvailable;
        applyAvailability();
      })
      .catch(function () { $('no-networks').hidden = false; });
  }

  $('rescan').addEventListener('click', refreshNetworks);

  $('connect').addEventListener('submit', function (event) {
    event.preventDefault();
    var ssid = $('ssid').value;
    var password = $('password').value;
    attempted = ssid;
    answersFrom = Infinity;
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
          answersFrom = polls + 1;
          message(onSetupNetwork
            ? 'Connecting to ' + ssid + '… The NubArca setup network will now close. '
              + 'If it appears again, the connection failed: join it again and reopen this page.'
            : 'Connecting to ' + ssid + '… The box stays reachable over Ethernet meanwhile.', 'ok');
          return;
        }
        busy(false);
        var reasons = {
          invalid_ssid: 'That Wi-Fi name is not valid.',
          invalid_password: 'A Wi-Fi password has 8 to 63 characters.',
          busy: 'Already connecting. Wait a moment.',
          unavailable: unavailableText()
        };
        message(reasons[r.body && r.body.error] || 'The request was not accepted.', 'bad');
      })
      .catch(function () {
        $('password').value = '';
        if (onSetupNetwork) {
          // The setup network closed before the answer arrived: that is the
          // normal hand-off, not an error. Its outcome comes with the setup
          // network, if that comes back.
          answersFrom = polls + 1;
          message('Connecting to ' + ssid + '… The NubArca setup network will now close.', 'ok');
          return;
        }
        busy(false);
        message('The request could not be sent. Try again.', 'bad');
      });
  });

  refreshStatus();
  refreshNetworks();
  setInterval(refreshStatus, 3000);
})();
