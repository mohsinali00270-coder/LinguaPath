// Lecture editor: Quill rich text with an "Edit HTML" fallback, and add/remove rows for examples, vocabulary and links.
(function () {
    var form = document.getElementById('lecture-form');
    if (!form) return;

    var textarea = document.getElementById('body-html');
    var editorEl = document.getElementById('editor');
    var htmlMode = false;
    var quill = null;

    if (window.Quill) {
        quill = new Quill(editorEl, {
            theme: 'snow',
            modules: {
                table: true,
                toolbar: {
                    container: [
                        [{ header: [2, 3, false] }],
                        ['bold', 'italic', 'underline', 'strike'],
                        [{ list: 'ordered' }, { list: 'bullet' }],
                        ['blockquote', 'link', 'image'],
                        [{ align: [] }],
                        ['clean']
                    ],
                    handlers: {
                        // Images by URL only (uploads arrive with media storage); pasted base64 images are removed on save.
                        image: function () {
                            var url = window.prompt('Image URL (https://…)');
                            if (!url) return;
                            var range = quill.getSelection(true);
                            quill.insertEmbed(range.index, 'image', url, 'user');
                        }
                    }
                }
            }
        });
        quill.clipboard.dangerouslyPasteHTML(textarea.value || '');
    } else {
        // CDN unreachable: fall back to editing HTML directly.
        editorEl.classList.add('d-none');
        textarea.classList.remove('d-none');
        htmlMode = true;
    }

    var toggle = document.getElementById('toggle-html');
    if (toggle) {
        toggle.addEventListener('click', function () {
            if (!quill) return;
            if (!htmlMode) {
                textarea.value = quill.getSemanticHTML();
                editorEl.parentElement.querySelector('.ql-toolbar').classList.add('d-none');
                editorEl.classList.add('d-none');
                textarea.classList.remove('d-none');
                toggle.textContent = 'Back to editor';
            } else {
                quill.setContents([]);
                quill.clipboard.dangerouslyPasteHTML(textarea.value || '');
                editorEl.parentElement.querySelector('.ql-toolbar').classList.remove('d-none');
                editorEl.classList.remove('d-none');
                textarea.classList.add('d-none');
                toggle.textContent = 'Edit HTML';
            }
            htmlMode = !htmlMode;
        });
    }

    // ---- AI assist (FR-A43): rewrites the editor text; nothing is saved until Save is pressed.
    var assist = document.getElementById('ai-assist');
    if (assist) {
        assist.addEventListener('click', function (e) {
            var btn = e.target.closest('[data-action]');
            if (!btn) return;
            var html = quill && !htmlMode ? quill.getSemanticHTML() : textarea.value;
            if (!html || html.replace(/<[^>]+>/g, '').trim().length === 0) { alert('Write an outline or some text first.'); return; }
            var data = new FormData();
            data.append('lectureId', assist.getAttribute('data-lecture'));
            data.append('action', btn.getAttribute('data-action'));
            data.append('html', html);
            var token = form.querySelector('input[name="__RequestVerificationToken"]');
            if (token) data.append('__RequestVerificationToken', token.value);
            assist.querySelectorAll('button').forEach(function (b) { b.disabled = true; });
            var label = btn.textContent;
            btn.textContent = 'Working…';
            fetch(assist.getAttribute('data-url'), { method: 'POST', body: data })
                .then(function (r) { return r.json(); })
                .then(function (res) {
                    if (res.error) { alert(res.error); return; }
                    if (!confirm('Replace the editor text with the AI version? You can still undo by not saving.')) return;
                    if (quill && !htmlMode) { quill.setContents([]); quill.clipboard.dangerouslyPasteHTML(res.html); }
                    else textarea.value = res.html;
                })
                .catch(function () { alert('The AI assist request failed.'); })
                .finally(function () {
                    assist.querySelectorAll('button').forEach(function (b) { b.disabled = false; });
                    btn.textContent = label;
                });
        });
    }

    // ---- dynamic rows
    document.querySelectorAll('[data-add-row]').forEach(function (btn) {
        btn.addEventListener('click', function () {
            var key = btn.getAttribute('data-add-row');
            var container = document.querySelector('[data-rows="' + key + '"]');
            var tpl = document.getElementById('tpl-' + key);
            var index = container.querySelectorAll('.lp-row').length;
            var html = tpl.innerHTML.replace(/__i__/g, index);
            container.insertAdjacentHTML('beforeend', html);
            var first = container.lastElementChild.querySelector('input');
            if (first) first.focus();
        });
    });

    document.addEventListener('click', function (e) {
        if (e.target.matches('[data-remove-row]')) {
            e.target.closest('.lp-row').remove();
        }
    });

    // Re-number rows so MVC binds a contiguous list, and copy the editor HTML into the form.
    form.addEventListener('submit', function () {
        document.querySelectorAll('[data-rows]').forEach(function (container) {
            var prefix = container.getAttribute('data-prefix');
            container.querySelectorAll('.lp-row').forEach(function (row, i) {
                row.querySelectorAll('[name]').forEach(function (input) {
                    input.name = input.name.replace(new RegExp('^' + prefix.replace('.', '\\.') + '\\[\\d+\\]'), prefix + '[' + i + ']');
                });
            });
        });
        if (quill && !htmlMode) textarea.value = quill.getSemanticHTML();
    });
})();
