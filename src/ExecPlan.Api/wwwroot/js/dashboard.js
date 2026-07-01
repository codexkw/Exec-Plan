// Task 16: realtime dashboard client. Dependency-free — assumes the vendored global `signalR`
// (wwwroot/lib/signalr/signalr.min.js, no CDN per NFR-6) and `fetch`. No-ops entirely on any page
// that doesn't render a live dashboard (e.g. the static post-close Summary, which intentionally
// omits this script per design §16 "no live updates").
//
// IMPORTANT: the server (Api/Hubs/SignalRRealtimeNotifier.cs) pushes the event name
// "DashboardUpdated" (NOT "DashboardChanged") and "ActivationClosed". Keep this in sync with that
// file if the push event names ever change.
(function () {
  'use strict';

  var root = document.querySelector('[data-activation-id]');
  if (!root) {
    return;
  }

  var id = root.getAttribute('data-activation-id');
  var snapshotUrl = '/admin/activations/' + id + '/snapshot';
  var summaryUrl = '/admin/activations/' + id + '/summary';
  var pollTimer = null;

  function setText(elementId, value) {
    var el = document.getElementById(elementId);
    if (el) {
      el.textContent = value;
    }
  }

  function setInner(elementId, html) {
    var el = document.getElementById(elementId);
    if (el) {
      el.innerHTML = html;
    }
  }

  function escapeHtml(value) {
    return String(value).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }

  // Re-renders ONLY the ranking table's <tbody id="rank-tbody"> (rows-only), so the SSR-rendered,
  // localized <thead> stays intact across pushes/polls (MUST-FIX 6). The `rank-best` (first) /
  // `rank-delayed` (last, when >1 team) classes are re-applied here with the exact same best/last-index
  // logic the Razor view uses — teams arrive already Score-descending from the service.
  function renderTeams(teams) {
    if (!teams || !teams.length) {
      return '';
    }
    var n = teams.length;
    return teams.map(function (t, i) {
      var rankClass = i === 0 ? 'rank-best' : (i === n - 1 && n > 1 ? 'rank-delayed' : '');
      return '<tr class="' + rankClass + '" data-team="' + escapeHtml(t.teamName) + '">' +
        '<td>' + escapeHtml(t.teamName) + '</td>' +
        '<td>' + t.members + '</td>' +
        '<td>' + t.readyCount + '</td>' +
        '<td>' + t.tasksDone + ' / ' + t.tasksTotal + '</td>' +
        '</tr>';
    }).join('');
  }

  function renderOverdue(overdue) {
    if (!overdue || !overdue.length) {
      return '<p class="text-muted">&mdash;</p>';
    }
    return '<ul class="list-group mb-3">' + overdue.map(function (o) {
      return '<li class="list-group-item text-danger">' + escapeHtml(o.title) + '</li>';
    }).join('') + '</ul>';
  }

  function renderFeed(events) {
    if (!events || !events.length) {
      return '<p class="text-muted">&mdash;</p>';
    }
    return '<ul class="list-group">' + events.map(function (e) {
      return '<li class="list-group-item">' + escapeHtml(e.text) + '</li>';
    }).join('') + '</ul>';
  }

  function render(dto) {
    if (!dto) {
      return;
    }

    if (dto.status === 1 /* ActivationStatus.Closed */) {
      window.location = summaryUrl;
      return;
    }

    setText('n-total', dto.totalParticipants);
    setText('n-pending', dto.pendingCount);
    setText('n-ready', dto.readyCount);
    setText('n-escalated', dto.escalatedCount);
    setText('n-inducted', dto.inductedCount);
    setText('n-response', Math.round(dto.responseRate * 100) + '%');
    setText('n-taskrate', Math.round(dto.taskCompletionRate * 100) + '%');

    setInner('rank-tbody', renderTeams(dto.teams));
    setInner('overdue-body', renderOverdue(dto.overdue));
    setInner('feed-body', renderFeed(dto.events));
  }

  function refresh() {
    return fetch(snapshotUrl, { credentials: 'same-origin' })
      .then(function (r) { return r.json(); })
      .then(render)
      .catch(function () { /* transient fetch failure — the poll timer or next reconnect retries */ });
  }

  function startPoll() {
    if (!pollTimer) {
      pollTimer = setInterval(refresh, 5000);
    }
  }

  function stopPoll() {
    if (pollTimer) {
      clearInterval(pollTimer);
      pollTimer = null;
    }
  }

  var conn = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/dashboard')
    .withAutomaticReconnect()
    .build();

  conn.on('DashboardUpdated', refresh);
  conn.on('ActivationClosed', function () { window.location = summaryUrl; });

  conn.onreconnecting(startPoll);
  conn.onreconnected(function () { stopPoll(); refresh(); });
  conn.onclose(startPoll);

  conn.start()
    .then(function () { return conn.invoke('JoinActivation', id); })
    .then(refresh)
    .catch(startPoll);
})();
