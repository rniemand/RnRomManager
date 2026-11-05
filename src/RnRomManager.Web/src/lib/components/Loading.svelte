<script lang="ts">
	interface Props {
		label?: string;
	}

	let { label = 'Loading…' }: Props = $props();
</script>

<div class="d-flex justify-content-center py-5" role="status">
	<div class="spinner-border text-secondary" aria-hidden="true"></div>
	<span class="visually-hidden">{label}</span>
</div>
