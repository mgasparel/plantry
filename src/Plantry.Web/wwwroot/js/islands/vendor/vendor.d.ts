// Curated ambient type declarations for the buildless island runtime (ADR-020 §6).
//
// Why hand-written rather than vendored upstream .d.ts: preact's and htm's
// published type trees pull in internal cross-file references (JSX namespace,
// ./src/* re-exports) and aren't served as a single self-contained file — so
// vendoring them faithfully is a dependency-tree-in-miniature, the exact thing
// buildless is meant to avoid. Declaring only the surface the islands actually
// use keeps type-checking fully resolvable with ZERO install (the editor's
// built-in TypeScript reads this via jsconfig `paths`), at the cost of these
// signatures being a curated subset we maintain. Re-pin alongside the .module.js
// versions when bumping the runtime.

declare module "preact" {
  export type ComponentChildren = unknown;
  export interface VNode { type: unknown; props: unknown; }
  export function h(type: unknown, props: unknown, ...children: unknown[]): VNode;
  export function render(vnode: VNode, parent: Element | DocumentFragment): void;
  export const Fragment: unknown;
}

declare module "@preact/signals-core" {
  export interface ReadonlySignal<T> { readonly value: T; peek(): T; }
  export interface Signal<T> { value: T; peek(): T; }
}

declare module "@preact/signals" {
  export interface ReadonlySignal<T> { readonly value: T; peek(): T; }
  export interface Signal<T> { value: T; peek(): T; }
  export function signal<T>(value: T): Signal<T>;
  export function computed<T>(fn: () => T): ReadonlySignal<T>;
  export function effect(fn: () => void): () => void;
}

declare module "htm" {
  interface Htm { bind(h: unknown): (strings: TemplateStringsArray, ...values: unknown[]) => import("preact").VNode; }
  const htm: Htm;
  export default htm;
}
