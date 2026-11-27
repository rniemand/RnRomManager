<script lang="ts">
	interface Props {
		/** The system's display name. */
		name: string;
	}

	let { name }: Props = $props();
</script>

<!-- The warning shared by ZipCompressionModal and RepackZipsModal. -->
<p>
	Every game ZIP of {name} is rewritten, one at a time. On a large library this takes a long time and
	uses a lot of disk and CPU. No other job runs and no game can be edited until it ends.
</p>
<p>If you cancel, some ZIPs keep the old level. They still play; use Repack to finish.</p>
<p class="mb-0">
	Each game needs free space about the size of its uncompressed ROMs while it's rewritten.
</p>
