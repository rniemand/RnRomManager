<script lang="ts">
	import type { Snippet } from 'svelte';
	import type { HTMLAnchorAttributes, HTMLButtonAttributes } from 'svelte/elements';
	import Icon from '#lib/icons/Icon.svelte';

	type Variant =
		'primary' | 'secondary' | 'success' | 'danger' | 'warning' | 'info' | 'light' | 'dark' | 'link';

	interface Props extends Omit<HTMLButtonAttributes, 'class'> {
		/** Bootstrap colour; omitted, no `btn` classes at all (`btn-close`, `nav-link`, `dropdown-item`…). */
		variant?: Variant;
		outline?: boolean;
		size?: 'sm' | 'lg';
		/** Icon name, before the children. */
		icon?: string;
		/** Shows a spinner (in place of `icon`, else before the children) and disables the button. */
		busy?: boolean;
		/** Renders an `<a class="btn">` instead of a `<button>`. */
		href?: string;
		target?: HTMLAnchorAttributes['target'];
		rel?: string;
		download?: HTMLAnchorAttributes['download'];
		class?: string;
		ref?: HTMLButtonElement | HTMLAnchorElement;
		children?: Snippet;
	}

	let {
		variant,
		outline = false,
		size,
		icon,
		busy = false,
		href,
		type = 'button',
		disabled = false,
		class: className,
		ref = $bindable(),
		children,
		...rest
	}: Props = $props();

	/** Between the icon or spinner and the children, as in today's markup. */
	const space = ' ';
	const classes = $derived(
		variant
			? ['btn', size && `btn-${size}`, `btn-${outline ? 'outline-' : ''}${variant}`, className]
			: className
	);
</script>

{#snippet content()}
	{#if busy}<span class="spinner-border spinner-border-sm" aria-hidden="true"
		></span>{:else if icon}<Icon
			name={icon}
		/>{/if}{#if (busy || icon) && children}{space}{/if}{@render children?.()}
{/snippet}

{#if href}
	<!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- callers pass a resolved path -->
	<a bind:this={ref} {href} class={classes} {...rest as HTMLAnchorAttributes}>{@render content()}</a
	>
{:else}
	<button bind:this={ref} {type} class={classes} disabled={busy || disabled} {...rest}
		>{@render content()}</button
	>
{/if}
