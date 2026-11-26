<script lang="ts" generics="T extends string">
	import type { HTMLAttributes } from 'svelte/elements';

	interface Props extends Omit<HTMLAttributes<HTMLDivElement>, 'class' | 'onchange'> {
		/** The radios' shared `name`; each `id` defaults to `{name}-{value}`. */
		name: string;
		options: { value: T; label: string; id?: string }[];
		value?: T;
		size?: 'sm' | 'lg';
		/** Disables every radio. */
		disabled?: boolean;
		/** Appended to the group's `btn-group`. */
		class?: string;
		onchange?: (value: T) => void;
	}

	let {
		name,
		options,
		value = $bindable(),
		size,
		disabled = false,
		class: className,
		onchange,
		...rest
	}: Props = $props();
</script>

<!-- A Bootstrap button group of `btn-check` radios; `role` and `aria-label` go through `...rest`. -->
<div class={['btn-group', className]} {...rest}>
	{#each options as option (option.value)}
		{@const id = option.id ?? `${name}-${option.value}`}
		<input
			type="radio"
			class="btn-check"
			{name}
			{id}
			value={option.value}
			autocomplete="off"
			{disabled}
			checked={value === option.value}
			onchange={() => {
				value = option.value;
				onchange?.(option.value);
			}}
		/>
		<label class={['btn btn-outline-secondary', size && `btn-${size}`]} for={id}
			>{option.label}</label
		>
	{/each}
</div>
