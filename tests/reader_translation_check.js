// Exercise the real method without loading a book or requiring a DOM library.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const script = fs.readFileSync(path.join(__dirname,
    '../TinyOPDS/Resources/reader/reader-main.js'), 'utf8');
// Only define the class; do not execute the page's DOMContentLoaded bootstrap.
const classEnd = script.indexOf('// Initialize reader when DOM is ready');
assert.ok(classEnd > 0, 'UniversalReader class was not found');
let notify;
const html = {className: '', lang: 'en'};
const context = vm.createContext({
    document: {documentElement: html},
    navigator: {language: 'de-DE'},
    MutationObserver: class {
        constructor(callback) { notify = callback; }
        observe(element, options) {
            assert.equal(element, html);
            assert.deepEqual(Array.from(options.attributeFilter), ['class', 'lang']);
        }
    }
});
vm.runInContext(script.slice(0, classEnd) + '\nthis.Reader = UniversalReader;', context);
let lang = 'uk';
const reader = {
    detectedLanguage: 'uk',
    bookContent: {getAttribute: () => lang, setAttribute: (key, value) => {
        assert.equal(key, 'lang'); lang = value;
    }}
};
assert.equal(typeof context.Reader.prototype.watchTranslation, 'function');
context.Reader.prototype.watchTranslation.call(reader);
html.className = 'translated-ltr';
html.lang = 'fr';
notify();
assert.equal(lang, 'fr');
html.lang = 'uk';
notify();
assert.equal(lang, 'de');
html.className = '';
notify();
assert.equal(lang, 'uk');
reader.bookContent = null;
assert.doesNotThrow(notify);
console.log('PASS reader translation: target language, fallback, restore, no book');
