<script lang="ts" generics="K extends string">
	import type { Snippet } from 'svelte';
	import Button from '#lib/components/forms/Button.svelte';
	import Icon from '#lib/icons/Icon.svelte';
	import { scrollActiveTab } from '#lib/scrollActiveTab.js';

	interface Tab {
		id: K;
		label: string;
		/** Link mode: the tab is an `<a>` with `aria-current`. */
		href?: string;
		icon?: string;
		/** Button mode: the tab's `id` and `aria-controls`. */
		tabId?: string;
		controls?: string;
		title?: string;
	}

	interface Props {
		tabs: readonly Tab[];
		active: K | null;
		/** Button mode: `role="tablist"` strip of `<button role="tab">`s. */
		onselect?: (id: K) => void;
		class?: string;
		linkClass?: string;
		/** U8 `use:scrollActiveTab`. */
		scroll?: boolean;
		/** Replaces the icon and label, for tabs with a badge or a custom label. */
		content?: Snippet<[Tab]>;
	}

	let {
		tabs,
		active,
		onselect,
		class: className,
		linkClass,
		scroll = true,
		content
	}: Props = $props();

	/** Between the icon and the label, as in today's markup. */
	const space = ' ';

	function linkClasses(isActive: boolean) {
		return ['nav-link', linkClass, isActive && 'active'].filter(Boolean).join(' ');
	}

	function scrollIf(list: HTMLElement, on: boolean) {
		return on ? scrollActiveTab(list) : undefined;
	}
</script>

{#snippet label(tab: Tab)}
	{#if content}{@render content(tab)}{:else}{#if tab.icon}<Icon
				name={tab.icon}
			/>{space}{/if}{tab.label}{/if}
{/snippet}

<ul
	class={['nav nav-tabs', className]}
	use:scrollIf={scroll}
	role={onselect ? 'tablist' : undefined}
>
	{#each tabs as tab (tab.id)}
		{@const isActive = tab.id === active}
		<li class="nav-item" role={onselect ? 'presentation' : undefined}>
			{#if onselect}
				<Button
					class={linkClasses(isActive)}
					id={tab.tabId}
					role="tab"
					aria-selected={isActive}
					aria-controls={tab.controls}
					title={tab.title}
					onclick={() => onselect(tab.id)}>{@render label(tab)}</Button
				>
			{:else}
				<!-- eslint-disable svelte/no-navigation-without-resolve -- callers pass resolved hrefs -->
				<a
					class={linkClasses(isActive)}
					aria-current={isActive ? 'page' : undefined}
					href={tab.href}
					title={tab.title}>{@render label(tab)}</a
				>
				<!-- eslint-enable svelte/no-navigation-without-resolve -->
			{/if}
		</li>
	{/each}
</ul>
