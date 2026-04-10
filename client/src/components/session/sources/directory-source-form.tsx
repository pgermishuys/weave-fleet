import { DirectoryPicker } from "@/components/session/directory-picker";

interface DirectorySourceFormProps {
  directory: string;
  isLoading: boolean;
  onDirectoryChange: (value: string) => void;
}

export function DirectorySourceForm({ directory, isLoading, onDirectoryChange }: DirectorySourceFormProps) {
  return (
    <div className="space-y-1.5">
      <label className="text-sm font-medium" htmlFor="directory">
        Directory
      </label>
      <DirectoryPicker
        id="directory"
        value={directory}
        onChange={onDirectoryChange}
        placeholder="/path/to/project"
        disabled={isLoading}
      />
    </div>
  );
}
