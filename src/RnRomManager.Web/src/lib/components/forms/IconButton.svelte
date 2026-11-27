<script lang="ts">
	import { fromAction } from 'svelte/attachments';
	import type { HTMLAnchorAttributes, HTMLButtonAttributes } from 'svelte/elements';
	import Button from '#lib/components/forms/Button.svelte';
	import { tooltip as tooltipAction } from '#lib/tooltip.js';

	type Tone = 'default' | 'danger' | 'success' | 'primary' | 'warning';

	interface Props extends Omit<HTMLButtonAttributes, 'class' | 'children'> {
		icon: string;
		/** The accessible name (`aria-label`). */
		label: string;
		/** Defaults to `label`. */
		tooltip?: string;
		tone?: Tone;
		/** Renders a link; `disabled` doesn't apply to it (callers hide it instead). */
		href?: string;
		download?: HTMLAnchorAttributes['download'];
		target?: HTMLAnchorAttributes['target'];
		rel?: string;
		class?: string;
	}

	let { icon, label, tooltip, tone = 'default', class: className, ...rest }: Props = $props();

	const toneClass: Record<Tone, string> = {
		default: 'text-body-secondary',
		danger: 'text-danger-emphasis',
		success: 'text-success-emphasis',
		primary: 'text-primary-emphasis',
		warning: 'text-warning-emphasis'
	};
</script>

<!-- I1: the house icon-only action, the look of the Game Metadata quick-edit pencil. -->
<Button
	variant="link"
	size="sm"
	class={[toneClass[tone], 'p-0', className].filter(Boolean).join(' ')}
	{icon}
	aria-label={label}
	{@attach fromAction(tooltipAction, () => tooltip ?? label)}
	{...rest}
/>
