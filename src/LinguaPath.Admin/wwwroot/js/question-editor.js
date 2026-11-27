// Question editor: shows the fields each type needs, adds/removes option rows and reloads lectures when the language changes.
(function () {
    var form = document.getElementById('question-form');
    if (!form) return;

    var typeSelect = document.getElementById('q-type');
    var hint = document.getElementById('type-hint');
    var optionsTitle = document.getElementById('options-title');
    var colText = document.getElementById('col-text');
    var container = document.getElementById('options');

    // Enum names by value (the select posts numbers).
    var names = { 1: 'MultipleChoice', 2: 'TrueFalse', 3: 'FillBlank', 4: 'MatchPairs', 5: 'ReorderWords', 6: 'Translate', 7: 'ListenChoose', 8: 'Speak', 9: 'Writing' };
    var hints = {
        MultipleChoice: 'One row per choice; tick every correct one (two or more correct lets learners pick several).',
        TrueFalse: 'Two rows, usually "True" and "False"; tick the correct one.',
        FillBlank: 'Write ___ (three underscores) in the question where the gap is. Rows are the accepted answers.',
        MatchPairs: 'Each row is a pair: left item and the item it matches. Right items are shuffled for the learner.',
        ReorderWords: 'First row is the correct sentence; it is cut into shuffled word tiles. Extra rows are other accepted orders.',
        Translate: 'Put the sentence to translate in "Sentence". Rows are accepted translations (case and punctuation are ignored).',
        ListenChoose: 'The learner hears "Sentence" (or the audio URL) and picks a choice. Tick the correct one.',
        Speak: 'The learner records themselves saying "Sentence". A reviewer grades it in the grading queue.',
        Writing: 'Free writing task. A reviewer grades it in the grading queue. Exams only.'
    };
    var titles = {
        FillBlank: ['Accepted answers', 'Answer'], Translate: ['Accepted translations', 'Translation'],
        ReorderWords: ['Correct sentence(s)', 'Sentence'], MatchPairs: ['Pairs', 'Left item']
    };

    function currentType() { return names[typeSelect.value] || typeSelect.value; }

    function apply() {
        var t = currentType();
        hint.textContent = hints[t] || '';
        var title = titles[t] || ['Options', 'Option'];
        optionsTitle.textContent = title[0];
        colText.textContent = title[1];
        document.querySelectorAll('[data-show]').forEach(function (el) {
            el.classList.toggle('d-none', el.getAttribute('data-show').split(' ').indexOf(t) < 0);
        });
        document.querySelectorAll('[data-hide]').forEach(function (el) {
            el.classList.toggle('d-none', el.getAttribute('data-hide').split(' ').indexOf(t) >= 0);
        });
        if (t === 'TrueFalse') {
            var texts = container.querySelectorAll('input[name$=".Text"]');
            var empty = Array.prototype.every.call(texts, function (i) { return !i.value; });
            if (empty && texts.length >= 2) { texts[0].value = 'True'; texts[1].value = 'False'; }
        }
    }

    typeSelect.addEventListener('change', apply);
    apply();

    document.getElementById('add-option').addEventListener('click', function () {
        var index = container.querySelectorAll('.lp-row').length;
        container.insertAdjacentHTML('beforeend', document.getElementById('option-row').innerHTML.replace(/__i__/g, index));
        apply();
        container.lastElementChild.querySelector('input').focus();
    });

    container.addEventListener('click', function (e) {
        if (e.target.matches('[data-remove-row]')) e.target.closest('.lp-row').remove();
    });

    form.addEventListener('submit', function () {
        container.querySelectorAll('.lp-row').forEach(function (row, i) {
            row.querySelectorAll('[name]').forEach(function (input) {
                input.name = input.name.replace(/^Input\.Options\[\d+\]/, 'Input.Options[' + i + ']');
            });
        });
    });

    // Reload lectures for the chosen language.
    var language = document.getElementById('q-language');
    var lecture = document.getElementById('q-lecture');
    language.addEventListener('change', function () {
        lecture.innerHTML = '<option value="">None (general question)</option>';
        if (!language.value || language.value === '0') return;
        fetch(lecture.getAttribute('data-url') + '?languageId=' + encodeURIComponent(language.value))
            .then(function (r) { return r.json(); })
            .then(function (items) {
                items.forEach(function (it) {
                    var o = document.createElement('option');
                    o.value = it.id;
                    o.textContent = it.label;
                    lecture.appendChild(o);
                });
            });
    });
})();
