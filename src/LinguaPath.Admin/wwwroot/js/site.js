// ---------------------------------------------------------------------------
// Light / dark theme toggle (saved per browser; _ThemeInit applies it before paint)
// and the off-canvas sidebar on small screens.
// ---------------------------------------------------------------------------
(function () {
    var root = document.documentElement;
    document.addEventListener('click', function (e) {
        var themeBtn = e.target.closest && e.target.closest('[data-lp-theme-toggle]');
        if (themeBtn) {
            var next = root.getAttribute('data-bs-theme') === 'dark' ? 'light' : 'dark';
            root.setAttribute('data-bs-theme', next);
            try { localStorage.setItem('lp-theme', next); } catch (err) { }
            return;
        }
        var navToggle = e.target.closest && e.target.closest('[data-lp-nav-toggle]');
        var shell = document.getElementById('lp-shell');
        if (navToggle && shell) {
            var open = shell.classList.toggle('lp-nav-open');
            navToggle.setAttribute('aria-expanded', open ? 'true' : 'false');
            return;
        }
        if (shell && e.target.closest && e.target.closest('[data-lp-nav-close]')) {
            shell.classList.remove('lp-nav-open');
            var t = document.querySelector('[data-lp-nav-toggle]');
            if (t) t.setAttribute('aria-expanded', 'false');
        }
    });
    document.addEventListener('keydown', function (e) {
        var shell = document.getElementById('lp-shell');
        if (e.key === 'Escape' && shell && shell.classList.contains('lp-nav-open')) shell.classList.remove('lp-nav-open');
    });
})();

// Confirm dangerous actions: <form data-confirm="Are you sure?">
document.addEventListener('submit', function (e) {
    var form = e.target;
    var message = form.getAttribute('data-confirm');
    if (message && !window.confirm(message)) {
        e.preventDefault();
    }
});

// ---------------------------------------------------------------------------
// Drag-and-drop ordering (roadmap builder, FR-A30).
// A container [data-sortable="group"] holds children with [draggable][data-id].
// Children can move between containers of the same group. After a drop, the new
// order of every touched container is POSTed as { parentId, ids } to its data-url.
// ---------------------------------------------------------------------------
(function () {
    var dragged = null;
    var sourceList = null;

    function token() {
        var input = document.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : '';
    }

    function status(message, isError) {
        var el = document.getElementById('lp-reorder-status');
        if (!el) return;
        el.textContent = message;
        el.className = 'small mb-2 ' + (isError ? 'text-danger' : 'text-success');
        if (!isError) setTimeout(function () { el.textContent = ''; }, 2000);
    }

    function itemsOf(list) {
        return Array.prototype.filter.call(list.children, function (c) { return c.hasAttribute('data-id'); });
    }

    function save(list) {
        var ids = itemsOf(list).map(function (c) { return parseInt(c.getAttribute('data-id'), 10); });
        return fetch(list.getAttribute('data-url'), {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token() },
            body: JSON.stringify({ parentId: parseInt(list.getAttribute('data-parent-id'), 10), ids: ids })
        }).then(function (r) {
            if (!r.ok) return r.json().then(function (b) { throw new Error(b && b.message ? b.message : 'Could not save the order.'); });
        });
    }

    function itemAfter(list, y) {
        var candidates = itemsOf(list).filter(function (c) { return c !== dragged; });
        for (var i = 0; i < candidates.length; i++) {
            var box = candidates[i].getBoundingClientRect();
            if (y < box.top + box.height / 2) return candidates[i];
        }
        return null;
    }

    document.addEventListener('dragstart', function (e) {
        var item = e.target.closest && e.target.closest('[draggable="true"][data-id]');
        if (!item) return;
        e.stopPropagation();
        dragged = item;
        sourceList = item.parentElement;
        item.classList.add('lp-dragging');
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', item.getAttribute('data-id'));
    });

    document.addEventListener('dragover', function (e) {
        if (!dragged) return;
        var list = e.target.closest && e.target.closest('[data-sortable]');
        // Only lists of the same kind (modules with modules, lectures with lectures).
        while (list && list.getAttribute('data-sortable') !== sourceList.getAttribute('data-sortable')) {
            list = list.parentElement && list.parentElement.closest('[data-sortable]');
        }
        if (!list) return;
        e.preventDefault();
        var empty = list.querySelector(':scope > .lp-empty');
        if (empty) empty.remove();
        var after = itemAfter(list, e.clientY);
        if (after) list.insertBefore(dragged, after);
        else list.appendChild(dragged);
    });

    document.addEventListener('dragend', function () {
        if (!dragged) return;
        var target = dragged.parentElement;
        dragged.classList.remove('lp-dragging');
        var lists = target === sourceList ? [target] : [target, sourceList];
        dragged = null;
        Promise.all(lists.map(save))
            .then(function () { status('Order saved.', false); })
            .catch(function (err) { status(err.message + ' Reload the page to see the saved order.', true); });
    });
})();
