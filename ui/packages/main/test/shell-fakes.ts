import type { MenuItemConstructorOptions } from 'electron';

export class FakeTray {
  menu: MenuItemConstructorOptions[] = [];
  tooltip = '';
  destroyed = false;
  private readonly listeners = new Map<string, (() => void)[]>();

  on(event: string, cb: () => void): this {
    this.listeners.set(event, [...(this.listeners.get(event) ?? []), cb]);
    return this;
  }

  emit(event: string): void {
    for (const cb of this.listeners.get(event) ?? []) cb();
  }

  setContextMenu(menu: MenuItemConstructorOptions[]): void {
    this.menu = menu;
  }

  setToolTip(tip: string): void {
    this.tooltip = tip;
  }

  isDestroyed(): boolean {
    return this.destroyed;
  }

  destroy(): void {
    this.destroyed = true;
  }

  item(id: string): MenuItemConstructorOptions {
    const found = this.menu.find((entry) => entry.id === id);
    if (found === undefined) throw new Error(`no menu item ${id}`);
    return found;
  }

  click(id: string): void {
    (this.item(id).click as () => void)();
  }

  layout(): string[] {
    return this.menu.map((entry) => (entry.type === 'separator' ? '---' : String(entry.label)));
  }
}
