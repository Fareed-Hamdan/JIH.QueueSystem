// Jordan Islamic Hospital – Queue System: small browser helpers used through Blazor JS interop.

window.jihQr = {
    /** Renders `text` as an SVG QR code into the element with id `elementId`. */
    render(elementId, text) {
        const el = document.getElementById(elementId);
        if (!el || !window.qrcode) return;
        const qr = qrcode(0, 'M');
        qr.addData(text);
        qr.make();
        el.innerHTML = qr.createSvgTag({ cellSize: 6, margin: 2, scalable: true });
        el.setAttribute('data-qr-text', text);
    },
    /** For static pages (print): renders every [data-qr] element. */
    renderAll() {
        document.querySelectorAll('[data-qr]').forEach((el, i) => {
            if (!el.id) el.id = 'qr-auto-' + i;
            window.jihQr.render(el.id, el.getAttribute('data-qr'));
        });
    }
};

window.jihPrint = {
    openTicket(url) {
        const w = window.open(url, 'jih-ticket', 'width=420,height=640');
        if (!w) location.href = url; // pop-up blocked
    }
};

/**
 * Waiting-hall voice. Calls by NUMBER and ROOM only, never by name.
 * Mode "Clips": plays pre-recorded files from /audio (works offline, can be replaced by human recordings
 * with the same file names). Mode "BrowserTts": uses the browser's speech engine.
 * If a clip is missing the announcement falls back to the browser voice.
 */
window.jihAnnouncer = {
    mode: 'Clips',
    english: true,
    unlocked: false,
    queue: [],
    busy: false,

    configure(mode, english) { this.mode = mode || 'Clips'; this.english = !!english; },

    unlock() {
        // One user gesture lets the page play sound for the rest of the session.
        try { new Audio('audio/chime.mp3').play().catch(() => { }); } catch { }
        try { if ('speechSynthesis' in window) speechSynthesis.speak(new SpeechSynthesisUtterance(' ')); } catch { }
        this.unlocked = true;
    },

    announce(ticketNumber, room) {
        this.queue.push({ ticketNumber, room });
        if (!this.busy) this._next();
    },

    async _next() {
        const item = this.queue.shift();
        if (!item) { this.busy = false; return; }
        this.busy = true;
        try {
            if (this.mode === 'BrowserTts') await this._speakTts(item);
            else {
                const ok = await this._playClips(item);
                if (!ok) await this._speakTts(item, true);
            }
        } catch (e) { console.warn('announce failed', e); }
        setTimeout(() => this._next(), 600);
    },

    _parts(ticketNumber) {
        // "A-015" → letter "A", number 15
        const m = /^([A-Z])-?0*(\d+)$/.exec(ticketNumber || '');
        return m ? { letter: m[1], num: parseInt(m[2], 10) } : null;
    },

    async _playClips(item) {
        const p = this._parts(item.ticketNumber);
        if (!p || p.num < 1 || p.num > 999 || item.room < 1 || item.room > 999) return false;
        const files = ['audio/chime.mp3',
            'audio/ar/number.mp3', `audio/ar/letters/${p.letter}.mp3`, `audio/ar/n/${p.num}.mp3`,
            'audio/ar/room.mp3', `audio/ar/n/${item.room}.mp3`];
        if (this.english) files.push('audio/pause.mp3',
            'audio/en/number.mp3', `audio/en/letters/${p.letter}.mp3`, `audio/en/n/${p.num}.mp3`,
            'audio/en/room.mp3', `audio/en/n/${item.room}.mp3`);
        for (const f of files) {
            const ok = await this._play(f);
            if (!ok && f !== 'audio/chime.mp3' && f !== 'audio/pause.mp3') return false;
        }
        return true;
    },

    _play(src) {
        return new Promise(resolve => {
            const a = new Audio(src);
            let done = false;
            const finish = ok => { if (!done) { done = true; resolve(ok); } };
            a.onended = () => finish(true);
            a.onerror = () => finish(false);
            setTimeout(() => finish(true), 6000); // never hang
            a.play().catch(() => finish(false));
        });
    },

    _speakTts(item, skipChime) {
        return new Promise(async resolve => {
            if (!('speechSynthesis' in window)) return resolve();
            if (!skipChime) await this._play('audio/chime.mp3');
            const p = this._parts(item.ticketNumber);
            const n = p ? `${p.letter} ${p.num}` : item.ticketNumber;
            const say = (text, lang) => new Promise(r => {
                const u = new SpeechSynthesisUtterance(text);
                u.lang = lang; u.rate = 0.9;
                const v = speechSynthesis.getVoices().find(v => v.lang && v.lang.toLowerCase().startsWith(lang.slice(0, 2)));
                if (v) u.voice = v;
                u.onend = r; u.onerror = r;
                speechSynthesis.speak(u);
                setTimeout(r, 9000);
            });
            await say(`الرقم ${n}، الرجاء التوجه إلى غرفة ${item.room}`, 'ar-SA');
            if (this.english) await say(`Number ${n}, please go to room ${item.room}`, 'en-US');
            resolve();
        });
    }
};

// Static pages (printed ticket): render QR codes and open the print dialog.
document.addEventListener('DOMContentLoaded', () => {
    window.jihQr.renderAll();
    if (document.querySelector('[data-autoprint]')) setTimeout(() => window.print(), 400);
});
