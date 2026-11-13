import { describe, expect, test } from 'bun:test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

const page = readFileSync(join(import.meta.dirname, '../doc/index.html'), 'utf8');
const script = page.match(/<script>([\s\S]*?)<\/script>/)[1];

function element(document, tagName) {
    const attributes = new Map();
    const classes = new Set();
    const listeners = {};
    return {
        tagName,
        inert: false,
        scrollTop: 0,
        clientHeight: 600,
        getAttribute: (name) => attributes.get(name) ?? null,
        setAttribute: (name, value) => attributes.set(name, String(value)),
        classList: {
            toggle: (name, force) => ((force ?? !classes.has(name)) ? classes.add(name) : classes.delete(name)),
            contains: (name) => classes.has(name),
            add: (name) => classes.add(name),
            remove: (name) => classes.delete(name),
        },
        addEventListener: (type, listener) => (listeners[type] ??= []).push(listener),
        dispatch: (type, event) => (listeners[type] ?? []).forEach((listener) => listener(event)),
        focus() {
            document.activeElement = this;
        },
    };
}

function openPage({ narrow }) {
    const listeners = {};
    const document = {
        activeElement: null,
        querySelectorAll: () => [],
        addEventListener: (type, listener) => (listeners[type] ??= []).push(listener),
        pressKey: (key) => (listeners.keydown ?? []).forEach((listener) => listener({ key })),
    };
    const toggle = element(document, 'BUTTON');
    const sidebar = element(document, 'ASIDE');
    document.getElementById = (id) => ({ menuToggle: toggle, sidebar })[id] ?? null;
    const media = { matches: narrow, listeners: [], addEventListener: (_type, listener) => media.listeners.push(listener) };
    const window = {
        scrollY: 0,
        matchMedia: () => media,
        addEventListener: () => {},
        requestAnimationFrame: (callback) => callback(),
    };
    new Function('window', 'document', script)(window, document);
    const expanded = () => toggle.getAttribute('aria-expanded');
    const open = () => sidebar.classList.contains('open');
    return { document, toggle, sidebar, media, expanded, open };
}

describe('the architecture reference navigation', () => {
    test('the menu button names the sidebar it controls and starts collapsed', () => {
        const button = page.match(/<button[^>]*id="menuToggle"[^>]*>/)[0];
        expect(button).toContain('aria-controls="sidebar"');
        expect(button).toContain('aria-expanded="false"');
        expect(page).toMatch(/<aside[^>]*id="sidebar"/);
    });

    test('on a narrow screen the closed sidebar is inert, and opening it exposes the links and says expanded', () => {
        const nav = openPage({ narrow: true });
        expect(nav.expanded()).toBe('false');
        expect(nav.sidebar.inert).toBe(true);
        nav.toggle.dispatch('click', {});
        expect(nav.open()).toBe(true);
        expect(nav.expanded()).toBe('true');
        expect(nav.sidebar.inert).toBe(false);
        nav.toggle.dispatch('click', {});
        expect(nav.open()).toBe(false);
        expect(nav.expanded()).toBe('false');
        expect(nav.sidebar.inert).toBe(true);
    });

    test('Escape closes the open sidebar, makes it inert again and returns focus to the menu button', () => {
        const nav = openPage({ narrow: true });
        nav.toggle.dispatch('click', {});
        nav.document.pressKey('Escape');
        expect(nav.open()).toBe(false);
        expect(nav.expanded()).toBe('false');
        expect(nav.sidebar.inert).toBe(true);
        expect(nav.document.activeElement).toBe(nav.toggle);
    });

    test('choosing a link closes the sidebar and says collapsed', () => {
        const nav = openPage({ narrow: true });
        nav.toggle.dispatch('click', {});
        nav.sidebar.dispatch('click', { target: { tagName: 'A' } });
        expect(nav.open()).toBe(false);
        expect(nav.expanded()).toBe('false');
        expect(nav.sidebar.inert).toBe(true);
    });

    test('on a wide screen the always-visible sidebar is never inert', () => {
        const nav = openPage({ narrow: false });
        expect(nav.sidebar.inert).toBe(false);
        nav.document.pressKey('Escape');
        expect(nav.sidebar.inert).toBe(false);
    });

    test('leaving the narrow layout closes the sidebar and lifts inert', () => {
        const nav = openPage({ narrow: true });
        nav.toggle.dispatch('click', {});
        nav.media.matches = false;
        nav.media.listeners.forEach((listener) => listener({ matches: false }));
        expect(nav.open()).toBe(false);
        expect(nav.expanded()).toBe('false');
        expect(nav.sidebar.inert).toBe(false);
    });
});
